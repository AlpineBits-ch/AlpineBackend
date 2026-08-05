using System.Security.Claims;
using Echo.Domain.Entities.Moderation;
using Echo.Domain.Enums;
using Echo.Moderation;
using Echo.Persistence.Persistance;
using Echo.RateLimiter;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Echo.Controllers;

/// <summary>The public support surface: open a ticket, follow it up, appeal a ban.</summary>
[ApiController]
[Route("api/v1/support")]
[EnableRateLimiting(GatewayRateLimiting.PolicyName)]
public class SupportController(
    MicroserviceContext context,
    ModerationMailer mailer,
    ILogger<SupportController> logger) : ControllerBase
{
    /// <summary>Opens a ticket.</summary>
    [HttpPost("tickets")]
    public async Task<IActionResult> OpenTicketAsync([FromBody] OpenTicketRequest request, CancellationToken ct)
    {
        if (!LooksLikeEmail(request.Email))
            return Failure(400, "email_invalid", "A contact email address is required.");

        if (string.IsNullOrWhiteSpace(request.Subject))
            return Failure(400, "subject_required", "A subject is required.");

        if (string.IsNullOrWhiteSpace(request.Body))
            return Failure(400, "body_required", "Tell us what is wrong.");

        if (!Enum.IsDefined(request.Category))
            return Failure(400, "category_invalid", "Unknown category.");

        var now = DateTimeOffset.UtcNow;
        var email = request.Email.Trim().ToLowerInvariant();

        // A cap on concurrently-open tickets per address, checked before anything is written.
        var openForAddress = await context.SupportTickets.CountAsync(t =>
            t.ContactEmail == email
            && t.Status != SupportTicketStatus.Resolved
            && t.Status != SupportTicketStatus.Closed, ct);

        if (openForAddress >= 5)
        {
            return Failure(429, "too_many_open_tickets",
                "You already have several open tickets. Please reply to one of those instead.");
        }

        var (ticket, token) = SupportTicket.Create(new CreateSupportTicketParams
        {
            ContactEmail = email,
            Subject = request.Subject,
            Category = request.Category,
            RequesterUserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
        }, now);

        var message = SupportTicketMessage.Create(
            ticket.Id, SupportMessageAuthorKind.Requester, ticket.RequesterUserId, request.Body, false, now);

        context.SupportTickets.Add(ticket);
        context.SupportTicketMessages.Add(message);

        await context.SaveChangesAsync(ct);

        mailer.QueueTicketOpened(ticket, token);

        logger.LogInformation("Support ticket {Reference} opened ({Category})", ticket.Reference, ticket.Category);

        return Ok(new
        {
            reference = ticket.Reference,
            token,
            message = "Keep this link - it is the only way back into your ticket.",
        });
    }

    [HttpGet("tickets/{reference}")]
    public async Task<IActionResult> GetTicketAsync(
        string reference, [FromQuery] string? token, CancellationToken ct)
    {
        var ticket = await FindTicketAsync(reference, token, ct);
        if (ticket is null) return TicketNotFound();

        var messages = await context.SupportTicketMessages.AsNoTracking()
            // Internal notes are excluded in the query, not in the serialiser.
            .Where(m => m.TicketId == ticket.Id && !m.IsInternal)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        return Ok(new PublicTicketDto
        {
            Reference = ticket.Reference,
            Subject = ticket.Subject,
            Category = ticket.Category.ToString(),
            Status = ticket.Status.ToString(),
            CreatedAt = ticket.CreatedAt,
            LastActivityAt = ticket.LastActivityAt,
            Messages = messages.Select(m => new PublicTicketMessageDto
            {
                // Never a staff member's name or id: a support reply is from the service, and naming
                // the individual invites the requester to route around the queue - or worse, to go
                // and find them.
                From = m.AuthorKind == SupportMessageAuthorKind.Requester ? "You" : "Support",
                Body = m.Body,
                CreatedAt = m.CreatedAt,
            }).ToList(),
        });
    }

    [HttpPost("tickets/{reference}/messages")]
    public async Task<IActionResult> ReplyAsync(
        string reference, [FromQuery] string? token, [FromBody] PublicReplyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
            return Failure(400, "body_required", "A reply needs a body.");

        var ticket = await FindTicketAsync(reference, token, ct, tracking: true);
        if (ticket is null) return TicketNotFound();

        if (ticket.Status == SupportTicketStatus.Closed)
            return Failure(409, "ticket_closed", "This ticket is closed. Open a new one.");

        var now = DateTimeOffset.UtcNow;

        // Appending to a Resolved ticket reopens it - Append moves it to AwaitingStaff.
        var message = ticket.Append(
            SupportMessageAuthorKind.Requester, ticket.RequesterUserId, request.Body, false, now);

        context.SupportTicketMessages.Add(message);
        await context.SaveChangesAsync(ct);

        return Ok(new PublicTicketMessageDto
        {
            From = "You",
            Body = message.Body,
            CreatedAt = message.CreatedAt,
        });
    }

    /// <summary>Appeals a ban or suspension.</summary>
    [HttpPost("appeals")]
    public async Task<IActionResult> SubmitAppealAsync([FromBody] SubmitAppealRequest request, CancellationToken ct)
    {
        if (!LooksLikeEmail(request.Email))
            return Failure(400, "email_invalid", "A contact email address is required.");

        if (string.IsNullOrWhiteSpace(request.Body))
            return Failure(400, "body_required", "Tell us why you think the decision was wrong.");

        var accepted = AppealAccepted();

        var normalised = PublicReference.Normalise(request.Reference);
        if (normalised is null)
        {
            logger.LogInformation("Appeal submitted with an unparseable reference");
            return accepted;
        }

        var action = await context.ModerationActions
            .Include(a => a.Appeals)
            .FirstOrDefaultAsync(a => a.Reference == normalised, ct);

        var now = DateTimeOffset.UtcNow;

        if (action is null || !action.IsAppealableAt(now))
        {
            logger.LogInformation("Appeal submitted against an unknown or non-appealable reference");
            return accepted;
        }

        // The one place this surface does say something specific.
        if (action.Appeals.Count > 0)
        {
            var existing = action.Appeals.First();

            return Conflict(new
            {
                code = existing.Status == AppealStatus.Denied ? "appeal_denied" : "already_appealed",
                message = existing.Status switch
                {
                    AppealStatus.Denied =>
                        "This decision was already appealed, and the appeal was declined. Each decision "
                        + "gets one appeal, so that outcome is final - submitting another will not reach "
                        + "a different person.",
                    AppealStatus.Granted =>
                        "This decision was already appealed and the appeal was accepted. If the "
                        + "restriction is still in place, it is being lifted - check back shortly.",
                    _ =>
                        "An appeal against this decision has already been submitted and is waiting to be "
                        + "read. You will be emailed the decision.",
                },
                status = existing.Status.ToString(),
                // Final is a property of the outcome, not of the appeal being open - so a client can
                // decide whether to keep offering the form without re-implementing this rule.
                final = existing.Status == AppealStatus.Denied,
                reference = existing.Reference,
            });
        }

        var appeal = ModerationAppeal.Create(new CreateAppealParams
        {
            ActionId = action.Id,
            ContactEmail = request.Email,
            SubmittedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
            Body = request.Body,
        }, now);

        context.ModerationAppeals.Add(appeal);

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The unique index on ActionId is the real guard; the check above is the friendly one.
            logger.LogInformation("Concurrent appeal submission against {Reference}", normalised);

            return Conflict(new
            {
                code = "already_appealed",
                message = "An appeal against this decision was already submitted.",
            });
        }

        logger.LogInformation("Appeal {Reference} filed against action {ActionReference}",
            appeal.Reference, action.Reference);

        return Ok(new
        {
            reference = appeal.Reference,
            message = "Your appeal was received. A person will read it and we will email you the decision.",
        });
    }

    /// <summary>Where an appeal has got to.</summary>
    [HttpGet("appeals/{reference}")]
    public async Task<IActionResult> GetAppealAsync(
        string reference, [FromQuery] string? email, CancellationToken ct)
    {
        var normalised = PublicReference.Normalise(reference);
        if (normalised is null || !LooksLikeEmail(email)) return AppealNotFound();

        var appeal = await context.ModerationAppeals.AsNoTracking()
            .Include(a => a.Action)
            .FirstOrDefaultAsync(a =>
                a.Reference == normalised && a.ContactEmail == email!.Trim().ToLowerInvariant(), ct);

        if (appeal is null) return AppealNotFound();

        return Ok(new
        {
            reference = appeal.Reference,
            status = appeal.Status.ToString(),
            submittedAt = appeal.CreatedAt,
            decidedAt = appeal.DecidedAt,
            // Only released once decided.
            decision = appeal.Status is AppealStatus.Granted or AppealStatus.Denied
                ? appeal.DecisionNote
                : null,
            // One appeal per decision, so a declined appeal is the end of it.
            final = appeal.Status == AppealStatus.Denied,
            actionReference = appeal.Action?.Reference,
        });
    }

    private async Task<SupportTicket?> FindTicketAsync(
        string reference, string? token, CancellationToken ct, bool tracking = false)
    {
        var normalised = PublicReference.Normalise(reference);
        if (normalised is null || string.IsNullOrWhiteSpace(token)) return null;

        var query = tracking
            ? context.SupportTickets.AsQueryable()
            : context.SupportTickets.AsNoTracking();

        var ticket = await query.FirstOrDefaultAsync(t => t.Reference == normalised, ct);

        // The token is compared in fixed time inside the entity.
        return ticket is not null && ticket.TokenMatches(token) ? ticket : null;
    }

    /// <summary>Deliberately loose.</summary>
    private static bool LooksLikeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.Trim();
        var at = trimmed.IndexOf('@');

        return at > 0
               && at < trimmed.Length - 1
               && trimmed.IndexOf('.', at) > at + 1
               && trimmed.Length <= 320
               && !trimmed.Contains(' ');
    }

    private IActionResult Failure(int status, string code, string message) =>
        StatusCode(status, new { code, message });

    /// <summary>The answer to every appeal submission that does not name a live, un-appealed action.
    /// Identical to the success body on purpose - see <see cref="SubmitAppealAsync"/>.</summary>
    private IActionResult AppealAccepted() => Ok(new
    {
        reference = (string?)null,
        message = "Your appeal was received. A person will read it and we will email you the decision.",
    });

    private IActionResult TicketNotFound() =>
        NotFound(new { code = "ticket_not_found", message = "No ticket matches that reference and link." });

    private IActionResult AppealNotFound() =>
        NotFound(new { code = "appeal_not_found", message = "No appeal matches that reference and address." });
}
