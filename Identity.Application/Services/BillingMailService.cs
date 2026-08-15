using System.Globalization;
using Identity.Application.Templates;
using Identity.Contracts.Bus.Commands;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Messaging;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Services;

/// <summary>The one call the billing mail path makes into a mail transport.</summary>
public interface IBillingMailSender
{
    Task SendAsync(string toAddress, string subject, string htmlBody, CancellationToken cancellationToken);
}

/// <summary>Microsoft Graph, through the same <see cref="EmailService"/> every other mail in this
/// service goes out on.</summary>
public class GraphBillingMailSender(EmailService mail) : IBillingMailSender
{
    public Task SendAsync(
        string toAddress, string subject, string htmlBody, CancellationToken cancellationToken) =>
        mail.SendEmailAsync(toAddress, subject, htmlBody);
}

/// <summary>Who a billing notification may be sent to, once the account has been checked.</summary>
public sealed record BillingMailRecipient(string Email, string DisplayName);

/// <summary>
/// The shared half of every billing mail: who may be written to, whether this one has already gone
/// out, and the record that says so.
/// </summary>
public class BillingMailService(
    IBillingMailSender sender,
    EmailTemplateRenderer renderer,
    ILogger<BillingMailService> logger)
{
    /// <summary>Beyond this many entitlement keys the mail lists none of them and says so with the
    /// plan name instead. A grant that touches thirty keys produces a wall of identifiers nobody
    /// reads.</summary>
    public const int MaxListedEntitlements = 8;

    /// <summary>The address to write to, or null when this account must not be mailed.</summary>
    public async Task<BillingMailRecipient?> RecipientAsync(
        MicroserviceContext ctx, string userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var user = await ctx.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.Email, u.UserName, u.UserType, u.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            logger.LogWarning("Billing notification for unknown account {UserId} was dropped", userId);
            return null;
        }

        if (user.UserType == UserType.Bot)
        {
            logger.LogInformation(
                "Billing notification for bot account {UserId} was dropped: bots have no mailbox", userId);
            return null;
        }

        if (user.Status is UserStatus.Deleted or UserStatus.PendingDeletion or UserStatus.PurgeInProgress)
        {
            logger.LogInformation(
                "Billing notification for {UserId} was dropped: the account is {Status}", userId, user.Status);
            return null;
        }

        // Belt to the status check's braces.
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            logger.LogWarning(
                "Billing notification for {UserId} was dropped: the account has no email address", userId);
            return null;
        }

        return new BillingMailRecipient(user.Email, user.UserName ?? user.Email);
    }

    /// <summary>
    /// Claims the right to send this notification exactly once, returning false when somebody
    /// already has.
    /// </summary>
    public async Task<bool> TryClaimAsync(
        MicroserviceContext ctx,
        string dedupeKey,
        string userId,
        string kind,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (await ctx.BillingNotifications.AsNoTracking()
                .AnyAsync(n => n.DedupeKey == dedupeKey, cancellationToken))
        {
            logger.LogInformation("Billing notification {Key} was already sent; not sending again", dedupeKey);
            return false;
        }

        var record = BillingNotificationRecord.Create(dedupeKey, userId, kind, now);
        ctx.BillingNotifications.Add(record);

        try
        {
            await ctx.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // Lost the race.
            ctx.Entry(record).State = EntityState.Detached;

            logger.LogInformation(
                "Billing notification {Key} was claimed concurrently; not sending again", dedupeKey);

            return false;
        }
    }

    // ── The three mails ──────────────────────────────────────────────────────

    public async Task SendCreditIssuedAsync(
        BillingMailRecipient recipient, CreditIssuedNotification message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(message);

        var body = await renderer.RenderAsync("CreditIssuedEmail.cshtml", new CreditIssuedEmail
        {
            Name = recipient.DisplayName,
            Email = recipient.Email,
            Points = Points(message.Points),
            BalancePoints = Points(message.BalancePoints),
            ExpiresOn = Date(message.ExpiresAt),
            FromCampaign = message.IssuedBy == CreditIssuedBy.Campaign,
            Disclaimer = message.Disclaimer,
        });

        await sender.SendAsync(
            recipient.Email, "Credit has been added to your Venta.gg account", body, cancellationToken);
    }

    public async Task SendGrantChangedAsync(
        BillingMailRecipient recipient,
        EntitlementGrantNotification message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(message);

        var (subject, headline, summary) = Copy(message);

        var body = await renderer.RenderAsync("EntitlementGrantEmail.cshtml", new EntitlementGrantEmail
        {
            Name = recipient.DisplayName,
            Email = recipient.Email,
            Headline = headline,
            Summary = summary,
            PlanDisplayName = message.PlanDisplayName,

            // A grant that names a plan already says everything useful with the plan's name, and a
            // grant that touches more keys than anyone will read says nothing useful by listing them.
            Entitlements = message.PlanDisplayName is null && message.Entitlements.Count <= MaxListedEntitlements
                ? [.. message.Entitlements]
                : [],
            ExpiresOn = Date(message.ExpiresAt),
            IsPermanent = message.Change != EntitlementGrantChange.Revoked && message.ExpiresAt is null,
        });

        await sender.SendAsync(recipient.Email, subject, body, cancellationToken);
    }

    public async Task SendPlanUpgradedAsync(
        BillingMailRecipient recipient, PlanUpgradedNotification message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(message);

        var body = await renderer.RenderAsync("PlanUpgradedEmail.cshtml", new PlanUpgradedEmail
        {
            Name = recipient.DisplayName,
            Email = recipient.Email,
            PlanDisplayName = message.PlanDisplayName,
            PreviousPlanDisplayName = message.PreviousPlanDisplayName,
            RenewsOn = Date(message.CurrentPeriodEnd),
        });

        await sender.SendAsync(
            recipient.Email, $"You are now on {message.PlanDisplayName}", body, cancellationToken);
    }

    /// <summary>The three wordings of the grant mail.</summary>
    private static (string Subject, string Headline, string Summary) Copy(
        EntitlementGrantNotification message) => message.Change switch
    {
        EntitlementGrantChange.Issued when message.PlanDisplayName is { } plan => (
            $"{plan} has been added to your Venta.gg account",
            $"You now have {plan}",
            $"we have added {plan} to your account."),

        EntitlementGrantChange.Issued => (
            "Something has been added to your Venta.gg account",
            "Something new on your account",
            "we have added some extras to your account."),

        EntitlementGrantChange.Amended => (
            "A change to your Venta.gg account",
            "Your access dates have changed",
            "we have changed how long something on your account runs for."),

        _ => (
            "A change to your Venta.gg account",
            "Something was removed from your account",
            "we have removed something that had been added to your account."),
    };

    /// <summary>A date as a person reads it.</summary>
    public static string Date(DateTimeOffset at) =>
        at.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    public static string? Date(DateTimeOffset? at) => at is null ? null : Date(at.Value);

    /// <summary>Points, with separators and never a currency symbol.</summary>
    public static string Points(long points) => points.ToString("N0", CultureInfo.InvariantCulture);
}
