using Social.Api.Dtos.Response;
using Social.Domain.Aggregate;
using Social.Infrastructure.Persistence;

namespace Social.Api.Services;

/// <summary>
/// Composes the three inputs every profile projection needs - the viewer's relation, the subject's
/// privacy record, and the per-viewer supplements - and hands them to <see cref="ProfileVisibility"/>.
///
/// <para>One service, used by both the REST controller and the cross-service bus handlers, so there
/// is exactly one answer to "what may this viewer see" and no way to reach a profile through a door
/// that skips it.</para>
/// </summary>
public class ProfileProjectionService(
    MicroserviceContext ctx,
    PrivacySettingsCache privacySettings,
    ISharedGuildResolver sharedGuilds,
    IIdentityProfileFactsResolver identityFacts)
{
    /// <summary>
    /// Projects <paramref name="subject"/> for <paramref name="viewerProfileId"/>. A null viewer -
    /// an unauthenticated or unidentifiable reader - is treated as a stranger, never as a friend.
    /// </summary>
    public async Task<ProfileDto> ProjectAsync(Profile subject, string? viewerProfileId, CancellationToken token = default)
    {
        var relation = viewerProfileId is null
            ? ViewerRelation.Other
            : await ctx.ResolveViewerRelationAsync(viewerProfileId, subject.Id);

        if (relation == ViewerRelation.Blocked) return ProfileVisibility.Minimal(subject);

        var settings = await privacySettings.GetAsync(subject.UserId, token);
        var supplements = await BuildSupplementsAsync(subject, viewerProfileId, relation, settings, token);

        return ProfileVisibility.Project(subject, settings, relation, supplements);
    }

    /// <summary>
    /// Resolves only the supplements the viewer is actually allowed to see. Gating the *query* as
    /// well as the projection keeps a hidden field from costing a join on every profile read, and
    /// means a bug in the projector cannot leak data the service never loaded.
    /// </summary>
    private async Task<ProfileSupplements> BuildSupplementsAsync(
        Profile subject,
        string? viewerProfileId,
        ViewerRelation relation,
        Identity.Contracts.Bus.Response.UserPrivacySettingsSummary settings,
        CancellationToken token)
    {
        IReadOnlyList<MutualFriendDto>? mutualFriends = null;
        IReadOnlyList<MutualServerDto>? mutualServers = null;
        IReadOnlyList<ProfileConnectionDto>? connections = null;
        DateOnly? birthday = null;

        // Self has no mutuals with themselves; skipping the queries is both correct and free.
        if (viewerProfileId is not null && relation != ViewerRelation.SelfView)
        {
            if (ProfileVisibility.CanView(settings.MutualFriendsVisibility, relation))
                mutualFriends = await ctx.MutualFriendsAsync(viewerProfileId, subject.Id);

            // Guild deliberately does not apply MutualServersVisibility on its side, so this
            // CanView is the only gate on the field - which is also why the lookup is inside it.
            //
            // Federated shadow guilds are included in what Guild returns, and that is the right
            // answer here: a guild materialized from a remote instance is a guild both people are
            // actually in, so the mutual-servers list would simply be wrong without them, and a
            // ServerMembers friend request would be refused between two people who share a space.
            // The contract's own invariant still holds - neither party learns anything they could
            // not read off that guild's member list.
            if (ProfileVisibility.CanView(settings.MutualServersVisibility, relation))
            {
                var viewer = await ctx.Profiles.FindAsync([viewerProfileId], token);
                if (viewer is not null)
                    mutualServers = await sharedGuilds.SharedGuildsAsync(viewer.UserId, subject.UserId, token);
            }
        }

        // Birthday and connections are properties of the subject alone, not of the (viewer, subject)
        // pair, so unlike the mutuals they are resolvable for a self-view too - a user reading their
        // own profile should see their own birthday. Both are fetched only when the gate has already
        // said the viewer may see them: a hidden field costs no bus call, and the projector cannot
        // leak what was never loaded.
        //
        // Birthday is the single most sensitive field on this surface - a full date of birth is
        // identity-theft-grade and it is what the minor floors are derived from - which is why
        // BirthdayVisibility's default of Nobody means the common case never issues this call at all.
        if (ProfileVisibility.CanView(settings.BirthdayVisibility, relation))
            birthday = await identityFacts.BirthdayAsync(subject.UserId, token);

        if (ProfileVisibility.CanView(settings.ConnectionsVisibility, relation))
            connections = await identityFacts.ConnectionsAsync(subject.UserId, token);

        return new ProfileSupplements
        {
            MutualFriends = mutualFriends,
            MutualServers = mutualServers,
            Connections = connections,
            Birthday = birthday,
            // Activity still has no source: rich presence belongs to Guild/Isle and neither reports
            // it here yet. Null is treated by the projector as "nothing to show" rather than
            // "nothing to hide", and the ShareActivity gate above it is already live, so wiring a
            // source in later cannot accidentally ship an ungated field. See ProfileSupplements.
            Activity = null,
        };
    }
}
