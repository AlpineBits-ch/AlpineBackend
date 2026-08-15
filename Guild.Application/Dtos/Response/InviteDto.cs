using Facet;
using Guild.Domain.Entity;
using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Response;

/// <summary><c>Members</c> - every member who joined on this invite, as raw GuildMember entities -
/// is excluded. It is the same expansion that made <see cref="FlatInviteDto"/> necessary when
/// InviteDto was nested inside MemberDto; nobody reads it, and UseCount already carries the only
/// part of it a client wants.</summary>
[Facet(typeof(GuildInvite), nameof(GuildInvite.Members), NestedFacets = [typeof(GuildDto), typeof(ChannelDto)])]
public partial class InviteDto
{
    /// <summary>The guild's welcome splash, so a client can render it on the invite-accept screen
    /// before the user is a member (and therefore before they could read it from the guild's own
    /// welcome-screen endpoint). Null when the guild has none or has it disabled. Not produced by
    /// the Facet mapping - the invite preview endpoints fill it in.</summary>
    public Request.UpdateWelcomeScreenDto? WelcomeScreen { get; set; }

    /// <summary>Overwrites the Facet-projected <c>State</c> with
    /// <see cref="GuildInvite.EffectiveState"/>, which is what every read path must hand out - the
    /// stored column does not know about expiry. Called by every endpoint that produces one of these,
    /// which is why the projection lives here rather than being repeated at each site.</summary>
    public InviteDto WithEffectiveState(GuildInvite invite, DateTimeOffset now)
    {
        State = invite.EffectiveState(now);
        return this;
    }
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
        nameof(GuildInvite.InviterId), nameof(GuildInvite.Temporary),
        nameof(GuildInvite.TargetType), nameof(GuildInvite.TargetUserId),
    ])]
public partial class FlatInviteDto
{

}
