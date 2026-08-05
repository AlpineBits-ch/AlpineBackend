using Domain;
using Social.Api.Dtos.Response;
using Social.Api.Services;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Services;

/// <summary>
/// The projection gate itself: T0-5 (Hidden never reaches a third party), T2-17 (the four field
/// visibilities, filtered in the projection rather than client-side) and T2-19 (ShareActivity).
/// </summary>
[TestFixture]
public class ProfileVisibilityTests
{
    private static Profile MakeProfile(OnlineStatus status = OnlineStatus.Online) => new()
    {
        Id = "prfl_1",
        UserId = "user-1",
        UserName = "subject",
        Bio = "hi",
        AccentColor = "#5865F2",
        Font = ProfileFont.Serif,
        OnlineStatus = status,
        LastSeenAt = DateTimeOffset.UtcNow,
    };

    private static ProfileSupplements FullSupplements() => new()
    {
        MutualFriends = [new MutualFriendDto { ProfileId = "prfl_m", UserId = "user-m", UserName = "mutual" }],
        MutualServers = [new MutualServerDto { GuildId = "guld_1", Name = "guild" }],
        Connections = [new ProfileConnectionDto { Type = "steam", ExternalId = "76561198000000000", Verified = true }],
        Birthday = new DateOnly(1990, 1, 2),
        Activities = [new Social.Contracts.Dtos.ActivityDto { Type = "Playing", Source = "Native", Name = "Isle" }],
    };

    // ── T0-5 presence ────────────────────────────────────────────────────────

    [TestCase(ViewerRelation.Other)]
    [TestCase(ViewerRelation.Friend)]
    [TestCase(ViewerRelation.Blocked)]
    public void ProjectStatus_HiddenRendersAsOfflineToEveryoneElse(ViewerRelation relation)
    {
        Assert.That(ProfileVisibility.ProjectStatus(OnlineStatus.Hidden, relation), Is.EqualTo(OnlineStatus.Offline));
    }

    [Test]
    public void ProjectStatus_SelfStillSeesHidden()
    {
        Assert.That(ProfileVisibility.ProjectStatus(OnlineStatus.Hidden, ViewerRelation.SelfView),
            Is.EqualTo(OnlineStatus.Hidden), "a user who chose invisible must still see that they chose it");
    }

    [TestCase(OnlineStatus.Online)]
    [TestCase(OnlineStatus.Idle)]
    [TestCase(OnlineStatus.DoNotDisturb)]
    [TestCase(OnlineStatus.Offline)]
    public void ProjectStatus_EveryOtherStatusPassesThroughUnchanged(OnlineStatus status)
    {
        Assert.That(ProfileVisibility.ProjectStatus(status, ViewerRelation.Other), Is.EqualTo(status));
    }

    [Test]
    public void Project_HiddenSubjectNeverReachesAThirdPartyPayload()
    {
        var dto = ProfileVisibility.Project(
            MakeProfile(OnlineStatus.Hidden), PrivacyTestHelpers.Defaults("user-1"), ViewerRelation.Friend);

        Assert.That(dto.OnlineStatus, Is.EqualTo(OnlineStatus.Offline));
    }

    // ── T2-17 field visibility ───────────────────────────────────────────────

    [TestCase(Visibility.Everyone, ViewerRelation.Other, true)]
    [TestCase(Visibility.Everyone, ViewerRelation.Friend, true)]
    [TestCase(Visibility.Everyone, ViewerRelation.Blocked, false)]
    [TestCase(Visibility.Friends, ViewerRelation.Friend, true)]
    [TestCase(Visibility.Friends, ViewerRelation.Other, false)]
    [TestCase(Visibility.Nobody, ViewerRelation.Friend, false)]
    [TestCase(Visibility.Nobody, ViewerRelation.Other, false)]
    [TestCase(Visibility.Nobody, ViewerRelation.SelfView, true)]
    public void CanView_MatchesTheSpecTable(Visibility visibility, ViewerRelation relation, bool expected)
    {
        Assert.That(ProfileVisibility.CanView(visibility, relation), Is.EqualTo(expected));
    }

    [Test]
    public void CanView_UnknownVisibilityValue_Refuses()
    {
        Assert.That(ProfileVisibility.CanView((Visibility)99, ViewerRelation.Friend), Is.False);
    }

    [Test]
    public void Project_FriendsOnlyFields_AreAbsentForAStranger()
    {
        var settings = PrivacyTestHelpers.Defaults("user-1"); // all four default to Friends/Nobody

        var dto = ProfileVisibility.Project(MakeProfile(), settings, ViewerRelation.Other, FullSupplements());

        Assert.Multiple(() =>
        {
            Assert.That(dto.MutualFriends, Is.Null, "a hidden field must be absent, not empty");
            Assert.That(dto.MutualServers, Is.Null);
            Assert.That(dto.Connections, Is.Null);
            Assert.That(dto.Birthday, Is.Null);
        });
    }

    [Test]
    public void Project_FriendsOnlyFields_ArePresentForAFriend()
    {
        var settings = PrivacyTestHelpers.Defaults("user-1");

        var dto = ProfileVisibility.Project(MakeProfile(), settings, ViewerRelation.Friend, FullSupplements());

        Assert.Multiple(() =>
        {
            Assert.That(dto.MutualFriends, Is.Not.Null.And.Count.EqualTo(1));
            Assert.That(dto.MutualServers, Is.Not.Null.And.Count.EqualTo(1));
            Assert.That(dto.Connections, Is.Not.Null.And.Count.EqualTo(1));
            Assert.That(dto.Birthday, Is.Null, "BirthdayVisibility defaults to Nobody");
        });
    }

    [Test]
    public void Project_BirthdayVisibilityEveryone_ExposesItToAStranger()
    {
        var settings = PrivacyTestHelpers.Defaults("user-1");
        settings.BirthdayVisibility = Visibility.Everyone;

        var dto = ProfileVisibility.Project(MakeProfile(), settings, ViewerRelation.Other, FullSupplements());

        Assert.That(dto.Birthday, Is.EqualTo(new DateOnly(1990, 1, 2)));
    }

    [Test]
    public void Project_RestrictiveDefaults_HideEverythingGated()
    {
        // What a viewer sees when the privacy lookup failed entirely.
        var dto = ProfileVisibility.Project(
            MakeProfile(), PrivacySettingsCache.RestrictiveDefaults("user-1"), ViewerRelation.Friend, FullSupplements());

        Assert.Multiple(() =>
        {
            Assert.That(dto.MutualFriends, Is.Null);
            Assert.That(dto.MutualServers, Is.Null);
            Assert.That(dto.Connections, Is.Null);
            Assert.That(dto.Birthday, Is.Null);
            Assert.That(dto.Activities, Is.Null);
        });
    }

    // ── T2-19 activity ───────────────────────────────────────────────────────

    [Test]
    public void Project_ShareActivityOff_DropsTheActivity()
    {
        var settings = PrivacyTestHelpers.Defaults("user-1");
        settings.ShareActivity = false;

        var dto = ProfileVisibility.Project(MakeProfile(), settings, ViewerRelation.Friend, FullSupplements());

        Assert.That(dto.Activities, Is.Null);
    }

    [Test]
    public void Project_ShareActivityOn_KeepsTheActivity()
    {
        var settings = PrivacyTestHelpers.Defaults("user-1");
        settings.ShareActivity = true;

        var dto = ProfileVisibility.Project(MakeProfile(), settings, ViewerRelation.Friend, FullSupplements());

        Assert.That(dto.Activities![0].Name, Is.EqualTo("Isle"));
    }

    [Test]
    public void Project_ShareActivityOff_StillShowsTheUserTheirOwnActivity()
    {
        var settings = PrivacyTestHelpers.Defaults("user-1");
        settings.ShareActivity = false;

        var dto = ProfileVisibility.Project(MakeProfile(), settings, ViewerRelation.SelfView, FullSupplements());

        Assert.That(dto.Activities, Is.Not.Null);
    }

    // ── T0-3 minimal projection ──────────────────────────────────────────────

    [Test]
    public void Project_BlockedViewer_GetsTheMinimalProjection()
    {
        var subject = MakeProfile(OnlineStatus.Online);

        var dto = ProfileVisibility.Project(
            subject, PrivacyTestHelpers.Defaults("user-1"), ViewerRelation.Blocked, FullSupplements());

        Assert.Multiple(() =>
        {
            Assert.That(dto.Id, Is.EqualTo("prfl_1"), "identity stays - a vanishing profile would announce the block");
            Assert.That(dto.UserName, Is.EqualTo("subject"));
            Assert.That(dto.Bio, Is.Null);
            Assert.That(dto.AccentColor, Is.Null);
            Assert.That(dto.Font, Is.EqualTo(ProfileFont.Default));
            Assert.That(dto.OnlineStatus, Is.EqualTo(OnlineStatus.Offline));
            Assert.That(dto.LastSeenAt, Is.EqualTo(default(DateTimeOffset)));
            Assert.That(dto.MutualFriends, Is.Null);
            Assert.That(dto.Activities, Is.Null);
        });
    }

    [Test]
    public void Project_DoesNotMutateTheSubjectEntity()
    {
        // The subject may be a tracked entity; rewriting its status during a read would persist a
        // viewer's projection as the user's actual state.
        var subject = MakeProfile(OnlineStatus.Hidden);

        ProfileVisibility.Project(subject, PrivacyTestHelpers.Defaults("user-1"), ViewerRelation.Other);

        Assert.That(subject.OnlineStatus, Is.EqualTo(OnlineStatus.Hidden));
    }
}
