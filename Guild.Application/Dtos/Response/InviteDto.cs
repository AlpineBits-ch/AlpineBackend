using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(GuildInvite), NestedFacets = [typeof(GuildDto), typeof(ChannelDto), typeof(ChannelDto)])]

public partial class InviteDto
{
    /// <summary>The guild's welcome splash, so a client can render it on the invite-accept screen
    /// before the user is a member (and therefore before they could read it from the guild's own
    /// welcome-screen endpoint). Null when the guild has none or has it disabled. Not produced by
    /// the Facet mapping - the invite preview endpoints fill it in.</summary>
    public Request.UpdateWelcomeScreenDto? WelcomeScreen { get; set; }
}

/// <summary>
/// The invite a member joined through, without the nested Guild and Channel objects.
/// </summary>
[Facet(typeof(GuildInvite),
    Include =
    [
        "Id", "CreatedAt", "UpdatedAt",
        nameof(GuildInvite.GuildId), nameof(GuildInvite.Type), nameof(GuildInvite.State),
        nameof(GuildInvite.Code), nameof(GuildInvite.ExpiresAt), nameof(GuildInvite.MaxUses),
        nameof(GuildInvite.UseCount), nameof(GuildInvite.ChannelId),
    ])]
public partial class FlatInviteDto
{

}