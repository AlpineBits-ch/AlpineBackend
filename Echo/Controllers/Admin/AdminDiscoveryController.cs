using Discovery.Contracts.Bus.Admin;
using Echo.Domain.Entities.Moderation;
using Echo.Moderation;
using Echo.Persistence.Persistance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wolverine;

namespace Echo.Controllers.Admin;

/// <summary>
/// Bans a guild out of the public directory. Discovery stays free of any notion of staff: the tier
/// check and the acting principal live here, and only a plain user id crosses the bus.
/// </summary>
[Authorize]
[Route("api/v1/admin/discovery")]
public class AdminDiscoveryController(
    MicroserviceContext context,
    StaffAccess staff,
    IMessageBus bus,
    ILogger<AdminDiscoveryController> logger)
    : AdminControllerBase(context, staff)
{
    /// <summary>Browse and search published listings, to find the guild before banning it.</summary>
    [HttpGet("listings")]
    public async Task<IActionResult> ListingsAsync(
        [FromQuery] string? query, [FromQuery] string? cursor, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        SearchDiscoveryListingsResponse response;
        try
        {
            response = await bus.InvokeAsync<SearchDiscoveryListingsResponse>(
                new SearchDiscoveryListingsRequest { Query = query, Cursor = cursor });
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Discovery did not answer a listing search by {ActorUserId}.", actor.UserId);
            return DiscoveryUnavailable();
        }

        return Ok(new { listings = response.Listings, nextCursor = response.NextCursor });
    }

    /// <summary>Active bans by default. Every ban, lifted ones included, with includeLifted=true.</summary>
    [HttpGet("bans")]
    public async Task<IActionResult> BansAsync([FromQuery] bool includeLifted, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        ListDiscoveryBansResponse response;
        try
        {
            response = await bus.InvokeAsync<ListDiscoveryBansResponse>(
                new ListDiscoveryBansRequest { IncludeLifted = includeLifted });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Discovery did not answer a ban list by {ActorUserId}.", actor.UserId);
            return DiscoveryUnavailable();
        }

        return Ok(new { bans = response.Bans });
    }

    [HttpPost("bans")]
    public async Task<IActionResult> BanAsync([FromBody] BanGuildRequest request, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        if (string.IsNullOrWhiteSpace(request.GuildId))
            return Failure(400, "guild_id_required", "Say which guild is being banned.");

        // Same rule as a takedown: a ban is a decision somebody may have to answer for.
        if (string.IsNullOrWhiteSpace(request.Reason))
            return Failure(400, "reason_required", "Say why this guild is being banned.");

        var reason = request.Reason.Trim();

        BanGuildFromDiscoveryResponse response;
        try
        {
            response = await bus.InvokeAsync<BanGuildFromDiscoveryResponse>(new BanGuildFromDiscoveryRequest
            {
                GuildId = request.GuildId,
                Reason = reason,
                StaffNote = string.IsNullOrWhiteSpace(request.StaffNote) ? null : request.StaffNote.Trim(),
                // The resolved principal, never the body - a ban audit trail a caller can forge is
                // not an audit trail.
                StaffUserId = actor.UserId,
                ExpiresAt = request.ExpiresAt,
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Discovery did not answer a ban of {GuildId} by {ActorUserId}.", request.GuildId, actor.UserId);
            return DiscoveryUnavailable();
        }

        Audit(actor, ModerationAuditActions.DiscoveryBanIssued, request.GuildId, reason);
        await Db.SaveChangesAsync(ct);

        logger.LogWarning("{ActorUserId} banned guild {GuildId} from discovery", actor.UserId, request.GuildId);

        return Ok(new { banId = response.BanId, guildId = request.GuildId });
    }

    [HttpDelete("bans/{guildId}")]
    public async Task<IActionResult> LiftBanAsync(string guildId, CancellationToken ct)
    {
        var actor = await ResolveStaffAsync();
        if (actor is null) return StaffForbidden();

        LiftDiscoveryBanResponse response;
        try
        {
            response = await bus.InvokeAsync<LiftDiscoveryBanResponse>(new LiftDiscoveryBanRequest
            {
                GuildId = guildId,
                StaffUserId = actor.UserId,
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Discovery did not answer a lift of {GuildId} by {ActorUserId}.", guildId, actor.UserId);
            return DiscoveryUnavailable();
        }

        // No active ban to lift is an outcome, not a failure - two moderators working the same case
        // should both see it succeed. Only an actual lift goes on the record.
        if (response.Lifted)
        {
            Audit(actor, ModerationAuditActions.DiscoveryBanLifted, guildId);
            await Db.SaveChangesAsync(ct);
        }

        return Ok(new { guildId, lifted = response.Lifted });
    }

    /// <summary>
    /// Discovery holds the ban state, so a change it did not acknowledge did not happen. Reported as
    /// a retry rather than as a refusal.
    /// </summary>
    private IActionResult DiscoveryUnavailable() =>
        Failure(503, "discovery_service_unavailable",
            "The discovery service did not answer. Nothing was changed - try again in a moment.");
}

/// <summary>A staff ban of a guild out of the discovery directory.</summary>
public class BanGuildRequest
{
    public string? GuildId { get; set; }

    /// <summary>Owner-facing. Required.</summary>
    public string? Reason { get; set; }

    /// <summary>Staff-facing only, never returned to a guild member.</summary>
    public string? StaffNote { get; set; }

    /// <summary>Null means indefinite.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
