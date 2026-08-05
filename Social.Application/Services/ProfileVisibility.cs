using Facet.Extensions;
using Identity.Contracts.Bus.Response;
using Domain;
using Social.Api.Dtos.Response;
using Social.Domain.Aggregate;
using Social.Domain.Enums;

namespace Social.Api.Services;

/// <summary>How the person doing the reading stands to the person being read.</summary>
public enum ViewerRelation
{
    /// <summary>The viewer is the subject.</summary>
    SelfView,

    /// <summary>Accepted friendship in Social's own graph.</summary>
    Friend,

    /// <summary>Any other authenticated reader.</summary>
    Other,

    /// <summary>A block exists in either direction.</summary>
    Blocked,
}

/// <summary>
/// The per-viewer extras a profile projection may carry, before the privacy gate is applied.
/// </summary>
public sealed record ProfileSupplements
{
    public static readonly ProfileSupplements None = new();

    public IReadOnlyList<MutualFriendDto>? MutualFriends { get; init; }
    public IReadOnlyList<MutualServerDto>? MutualServers { get; init; }
    public IReadOnlyList<ProfileConnectionDto>? Connections { get; init; }
    public DateOnly? Birthday { get; init; }

    /// <summary>
    /// Rich presence, in the same shape the rest of the system uses (<see
    /// cref="Social.Contracts.Dtos.ActivityDto"/>).
    /// </summary>
    public IReadOnlyList<Social.Contracts.Dtos.ActivityDto>? Activities { get; init; }
}

/// <summary>
/// The single projection gate every profile in Social passes through (privacy spec T0-5, T2-17,
/// T2-19).
/// </summary>
public static class ProfileVisibility
{
    /// <summary>T0-5.</summary>
    public static OnlineStatus ProjectStatus(OnlineStatus status, ViewerRelation relation)
    {
        if (relation == ViewerRelation.SelfView) return status;
        if (relation == ViewerRelation.Blocked) return OnlineStatus.Offline;
        return status == OnlineStatus.Hidden ? OnlineStatus.Offline : status;
    }

    /// <summary>T2-17's visibility table.</summary>
    public static bool CanView(Visibility visibility, ViewerRelation relation) => relation switch
    {
        ViewerRelation.SelfView => true,
        ViewerRelation.Blocked => false,
        _ => visibility switch
        {
            Visibility.Everyone => true,
            Visibility.Friends => relation == ViewerRelation.Friend,
            Visibility.Nobody => false,
            // Unknown value from a newer Identity: refuse. Fail closed, per the cross-cutting rules.
            _ => false,
        },
    };

    /// <summary>
    /// Projects <paramref name="subject"/> for <paramref name="relation"/>, applying the presence
    /// rule and every field-visibility gate.
    /// </summary>
    public static ProfileDto Project(
        Profile subject,
        UserPrivacySettingsSummary settings,
        ViewerRelation relation,
        ProfileSupplements? supplements = null)
    {
        if (relation == ViewerRelation.Blocked) return Minimal(subject);

        supplements ??= ProfileSupplements.None;

        // Project off a detached copy: the caller's entity may be tracked, and rewriting
        // OnlineStatus on a tracked instance would persist the projection on the next
        // SaveChanges - a viewer's read is not allowed to change what the subject actually is.
        var view = Copy(subject);
        view.OnlineStatus = ProjectStatus(subject.OnlineStatus, relation);

        var dto = view.ToFacet<Profile, ProfileDto>();

        if (CanView(settings.MutualFriendsVisibility, relation))
            dto.MutualFriends = supplements.MutualFriends ?? [];

        if (CanView(settings.MutualServersVisibility, relation))
            dto.MutualServers = supplements.MutualServers ?? [];

        if (CanView(settings.ConnectionsVisibility, relation))
            dto.Connections = supplements.Connections ?? [];

        if (CanView(settings.BirthdayVisibility, relation))
            dto.Birthday = supplements.Birthday;

        // T2-19: ShareActivity is a plain boolean, not a Visibility - "off" means nobody but the
        // user themselves, which is what SelfView already short-circuits.
        if (relation == ViewerRelation.SelfView || settings.ShareActivity)
            dto.Activities = supplements.Activities;

        return dto;
    }

    /// <summary>
    /// The minimal public projection a blocked reader gets (privacy spec T0-3): enough to render a
    /// name and avatar where the id already appears, and nothing that carries information the
    /// blocker chose to withhold.
    /// </summary>
    public static ProfileDto Minimal(Profile subject)
    {
        var stripped = new Profile
        {
            Id = subject.Id,
            UserId = subject.UserId,
            UserName = subject.UserName,
            FederatedServerId = subject.FederatedServerId,
            Bio = null,
            AccentColor = null,
            Font = ProfileFont.Default,
            OnlineStatus = OnlineStatus.Offline,
            LastSeenAt = default,
            CreatedAt = subject.CreatedAt,
            UpdatedAt = subject.CreatedAt,
        };

        return stripped.ToFacet<Profile, ProfileDto>();
    }

    private static Profile Copy(Profile subject) => new()
    {
        Id = subject.Id,
        UserId = subject.UserId,
        UserName = subject.UserName,
        FederatedServerId = subject.FederatedServerId,
        Bio = subject.Bio,
        AccentColor = subject.AccentColor,
        Font = subject.Font,
        OnlineStatus = subject.OnlineStatus,
        LastSeenAt = subject.LastSeenAt,
        CreatedAt = subject.CreatedAt,
        UpdatedAt = subject.UpdatedAt,
    };
}
