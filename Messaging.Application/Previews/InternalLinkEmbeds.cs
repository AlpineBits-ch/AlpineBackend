using Bots.Contracts.Gateway.Payloads;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Domain.Previews;
using Wolverine;

namespace Messaging.Application.Previews;

/// <summary>
/// Turns a recognized instance-local link into a card, without ever leaving the cluster.
/// </summary>
public static class InternalLinkEmbeds
{
    /// <summary>Resolves one link.</summary>
    public static async Task<EmbedPayload?> ResolveAsync(InternalLink link, IMessageBus bus) =>
        link.Kind switch
        {
            InternalLinkKind.Invite => await ResolveInviteAsync(link, bus),
            InternalLinkKind.WikiPage => WikiPageStub(link),
            _ => null,
        };

    private static async Task<EmbedPayload?> ResolveInviteAsync(InternalLink link, IMessageBus bus)
    {
        var response = await bus.InvokeAsync<ResolveInviteCardResponse>(new ResolveInviteCardRequest
        {
            Code = link.Value("code"),
        });

        return response.Invite is null ? null : InviteCard(link, response.Invite);
    }

    private static EmbedPayload InviteCard(InternalLink link, InviteCardInfo invite)
    {
        var embed = new EmbedPayload
        {
            Type = EmbedTypes.VentaInvite,
            Url = link.Url,
            Title = invite.GuildName,
            Description = invite.GuildDescription,
            Venta = new EmbedVentaPayload
            {
                Kind = "invite",
                Resolved = true,
                InviteCode = invite.Code,
                GuildId = invite.GuildId,
                ChannelId = invite.ChannelId,
                ExpiresAt = invite.ExpiresAt,
                MaxUses = invite.MaxUses,
            },
        };

        // A guild's name and description are typed by its owner, so they are third-party text on
        // exactly the same footing as a scraped og:title - and they land in the same 6000-character
        // budget shared with every other embed on the message.
        return EmbedLimits.Clamp(embed);
    }

    /// <summary>Identity and nothing else.</summary>
    private static EmbedPayload WikiPageStub(InternalLink link) => new()
    {
        Type = EmbedTypes.VentaWikiPage,
        Url = link.Url,
        Venta = new EmbedVentaPayload
        {
            Kind = "wiki_page",
            Resolved = false,
            GuildId = link.Value("guildId"),
            PageId = link.Value("pageId"),
        },
    };
}
