using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

/// <summary>Answers the invite half of Messaging's internal-link previews.</summary>
public class ResolveInviteCardHandler
{
    public static async Task<ResolveInviteCardResponse> Handle(
        ResolveInviteCardRequest request,
        MicroserviceContext ctx,
        VanityUrlService vanity)
    {
        if (string.IsNullOrWhiteSpace(request.Code)) return new ResolveInviteCardResponse();

        var invite = await ctx.GuildInvites
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Code == request.Code);

        invite ??= await vanity.ResolveInviteAsync(request.Code);

        // A revoked invite gets no card, matching the 404 the public preview answers with.
        if (invite is null || invite.State == InviteState.Revoked) return new ResolveInviteCardResponse();

        // Projected rather than Include(i => i.Guild): a guild aggregate drags its collections,
        // PublicKeys included, and this needs two strings off it.
        var guild = await ctx.Guilds
            .AsNoTracking()
            .Where(g => g.Id == invite.GuildId)
            .Select(g => new { g.Name, g.Description })
            .FirstOrDefaultAsync();

        if (guild is null) return new ResolveInviteCardResponse();

        return new ResolveInviteCardResponse
        {
            Invite = new InviteCardInfo
            {
                Code = invite.Code,
                GuildId = invite.GuildId,
                GuildName = guild.Name,
                GuildDescription = guild.Description,
                ChannelId = invite.ChannelId,
                ExpiresAt = invite.ExpiresAt,
                MaxUses = invite.MaxUses,
            },
        };
    }
}
