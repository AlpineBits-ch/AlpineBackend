using Social.Api.Dtos.Response;
using Social.Domain.Aggregate;
using Social.Infrastructure.Persistence;

namespace Social.Api.Services;

/// <summary>
/// Composes the three inputs every profile projection needs - the viewer's relation, the subject's
/// privacy record, and the per-viewer supplements - and hands them to <see
/// cref="ProfileVisibility"/>.
/// </summary>
public class ProfileProjectionService(
    MicroserviceContext ctx,
    PrivacySettingsCache privacySettings,
    ISharedGuildResolver sharedGuilds,
    IIdentityProfileFactsResolver identityFacts)
{
    /// <summary>
    /// Projects <paramref name="subject"/> for <paramref name="viewerProfileId"/>.
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

    /// <summary>Resolves only the supplements the viewer is actually allowed to see.</summary>
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
            if (ProfileVisibility.CanView(settings.MutualServersVisibility, relation))
            {
                var viewer = await ctx.Profiles.FindAsync([viewerProfileId], token);
                if (viewer is not null)
                    mutualServers = await sharedGuilds.SharedGuildsAsync(viewer.UserId, subject.UserId, token);
            }
        }

        // Birthday and connections are properties of the subject alone, not of the (viewer,
        // subject) pair, so unlike the mutuals they are resolvable for a self-view too - a user
        // reading their own profile should see their own birthday.
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
            // it here yet.
            Activity = null,
        };
    }
}
