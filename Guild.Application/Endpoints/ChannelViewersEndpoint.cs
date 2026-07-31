using System.Security.Claims;
using Guild.Application.Services;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

public class ChannelViewersDto
{
    public string ChannelId { get; set; } = null!;

    /// <summary>Everyone who currently holds ViewChannel here.</summary>
    public List<string> UserIds { get; set; } = new();
}

/// <summary>
/// Who can actually see a channel.
///
/// <para>Exists because end-to-end encryption needs the exact roster. The client previously fell
/// back to the whole guild member list, which for a channel with restrictive overwrites handed
/// group keys to people who cannot open the channel at all - readable traffic for an audience wider
/// than the channel itself.</para>
/// </summary>
[Authorize]
public class ChannelViewersEndpoint
{
    /// <summary>
    /// Resolved one member at a time through <see cref="GuildPermissionService"/> rather than by
    /// reimplementing the role/overwrite arithmetic in a bulk query.
    ///
    /// <para>That is deliberate. A second implementation of "who can see this channel" would be
    /// free to drift from the one that actually gates reads, and the failure mode of drift here is
    /// handing someone the keys to a room they cannot enter. The per-member checks are cached, and
    /// this runs when encryption is toggled or a roster is reconciled - not on the message path.</para>
    /// </summary>
    [WolverineGet("/api/v1/channels/{channelId}/viewers")]
    public static async Task<IResult> GetChannelViewers(
        string channelId,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var channel = await ctx.Channels
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == channelId);

        if (channel is null) return Results.NotFound();

        // You have to be able to see the channel to ask who else can.
        if (!await permissionService.CanUserPerformActionAsync(userId, channelId, Permissions.ViewChannel))
            return Results.Forbid();

        var memberIds = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == channel.GuildId)
            .Select(m => m.UserId)
            .ToListAsync();

        var viewers = new List<string>();
        foreach (var memberId in memberIds)
        {
            if (await permissionService.CanUserPerformActionAsync(memberId, channelId, Permissions.ViewChannel))
                viewers.Add(memberId);
        }

        return Results.Ok(new ChannelViewersDto { ChannelId = channelId, UserIds = viewers });
    }
}
