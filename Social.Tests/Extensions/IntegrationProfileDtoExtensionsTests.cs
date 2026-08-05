using AppEnvironment;
using Social.Api.Extensions;
using Social.Domain.Aggregate;
using DomainRelationshipStatus = Social.Domain.Enums.RelationshipStatus;
using ContractRelationshipStatus = Social.Contracts.Dtos.RelationshipStatus;

namespace Social.Tests.Extensions;

[TestFixture]
public class IntegrationProfileDtoExtensionsTests
{
    private string _originalInstanceUrl = null!;

    [SetUp]
    public void SetUp()
    {
        _originalInstanceUrl = Env.GeneralConfiguration.InstanceUrl;
        Env.GeneralConfiguration.InstanceUrl = "https://chat.example.org";
    }

    [TearDown]
    public void TearDown() => Env.GeneralConfiguration.InstanceUrl = _originalInstanceUrl;

    private static Profile MakeProfile(string id = "prfl_1", string userId = "user-1") => new()
    {
        Id = id,
        UserId = userId,
        UserName = "tester",
        Bio = "hi",
        AccentColor = "#5865F2",
    };

    [Test]
    public void ToIntegrationProfile_MapsScalarFieldsAndBuildsMediaUrls()
    {
        var profile = MakeProfile("prfl_42");

        var dto = profile.ToIntegrationProfile();

        Assert.Multiple(() =>
        {
            Assert.That(dto.Id, Is.EqualTo("prfl_42"));
            Assert.That(dto.UserName, Is.EqualTo("tester"));
            Assert.That(dto.Bio, Is.EqualTo("hi"));
            Assert.That(dto.UserId, Is.EqualTo("user-1"));
            Assert.That(dto.AccentColor, Is.EqualTo("#5865F2"));
            Assert.That(dto.Font, Is.EqualTo("Default"));
            Assert.That(dto.AvatarUrl, Is.EqualTo("https://chat.example.org/api/v1/social/profiles/prfl_42/avatar"));
            Assert.That(dto.BannerUrl, Is.EqualTo("https://chat.example.org/api/v1/social/profiles/prfl_42/banner"));
        });
    }

    // ── Media URLs are built from INSTANCE_URL, not from Alpinebits' host ──────

    [Test]
    public void ToIntegrationProfile_BuildsMediaUrlsFromTheConfiguredInstanceUrl()
    {
        Env.GeneralConfiguration.InstanceUrl = "https://selfhosted.example.net";

        var dto = MakeProfile("prfl_42").ToIntegrationProfile();

        Assert.Multiple(() =>
        {
            Assert.That(dto.AvatarUrl, Is.EqualTo("https://selfhosted.example.net/api/v1/social/profiles/prfl_42/avatar"));
            Assert.That(dto.BannerUrl, Is.EqualTo("https://selfhosted.example.net/api/v1/social/profiles/prfl_42/banner"));
        });
    }

    [Test]
    public void ToIntegrationProfile_TrailingSlashOnInstanceUrl_DoesNotDoubleTheSeparator()
    {
        // INSTANCE_URL is operator-written, so a trailing slash is a matter of taste rather than a
        // misconfiguration - it must not produce https://host//api/v1/...
        Env.GeneralConfiguration.InstanceUrl = "https://selfhosted.example.net/";

        var dto = MakeProfile("prfl_42").ToIntegrationProfile();

        Assert.Multiple(() =>
        {
            Assert.That(dto.AvatarUrl, Is.EqualTo("https://selfhosted.example.net/api/v1/social/profiles/prfl_42/avatar"));
            Assert.That(dto.BannerUrl, Is.EqualTo("https://selfhosted.example.net/api/v1/social/profiles/prfl_42/banner"));
        });
    }

    [Test]
    public void ToIntegrationProfile_MapsEmptyRelationshipsToEmptyList()
    {
        var profile = MakeProfile();

        var dto = profile.ToIntegrationProfile();

        Assert.That(dto.Relationships, Is.Empty);
    }

    [Test]
    public void ToIntegrationProfile_MapsRelationshipsUsingTargetProfile()
    {
        var target = MakeProfile("prfl_target", "user-target");

        var profile = MakeProfile();
        profile.Relationships.Add(new Relationship
        {
            Id = "rlsp_1",
            OwnerId = profile.Id,
            TargetId = target.Id,
            Target = target,
            Status = DomainRelationshipStatus.Friends,
        });

        var dto = profile.ToIntegrationProfile();

        var relationship = dto.Relationships.Single();
        Assert.Multiple(() =>
        {
            Assert.That(relationship.Id, Is.EqualTo("rlsp_1"));
            Assert.That(relationship.ProfileId, Is.EqualTo("prfl_target"));
            Assert.That(relationship.UserId, Is.EqualTo("user-target"));
            Assert.That(relationship.Status, Is.EqualTo(ContractRelationshipStatus.Accepted));
        });
    }

    [TestCase(DomainRelationshipStatus.None, ContractRelationshipStatus.None)]
    [TestCase(DomainRelationshipStatus.PendingIncoming, ContractRelationshipStatus.Pending)]
    [TestCase(DomainRelationshipStatus.PendingOutgoing, ContractRelationshipStatus.Pending)]
    [TestCase(DomainRelationshipStatus.Friends, ContractRelationshipStatus.Accepted)]
    [TestCase(DomainRelationshipStatus.Blocked, ContractRelationshipStatus.Blocked)]
    public void ToIntegrationEnum_MapsEveryDomainStatus(DomainRelationshipStatus domainStatus, ContractRelationshipStatus expected)
    {
        Assert.That(domainStatus.ToIntegrationEnum(), Is.EqualTo(expected));
    }

    [Test]
    public void ToIntegrationEnum_UnknownValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((DomainRelationshipStatus)999).ToIntegrationEnum());
    }

    // ── T0-3: the block list does not ride along on an ordinary profile lookup ─

    [Test]
    public void ToIntegrationProfile_OmitsBlockedRelationships()
    {
        // This projection is cached and handed to whichever service asked, and from there it can
        // reach a client.
        var friend = MakeProfile("prfl_friend", "user-friend");
        var blocked = MakeProfile("prfl_blocked", "user-blocked");

        var profile = MakeProfile();
        profile.Relationships.Add(new Relationship
        {
            Id = "rlsp_friend", OwnerId = profile.Id, TargetId = friend.Id, Target = friend,
            Status = DomainRelationshipStatus.Friends,
        });
        profile.Relationships.Add(new Relationship
        {
            Id = "rlsp_blocked", OwnerId = profile.Id, TargetId = blocked.Id, Target = blocked,
            Status = DomainRelationshipStatus.Blocked,
        });

        var dto = profile.ToIntegrationProfile();

        Assert.That(dto.Relationships.Select(r => r.Id), Is.EquivalentTo(new[] { "rlsp_friend" }));
    }

    [Test]
    public void ToMinimalIntegrationProfile_KeepsIdentityAndDropsEverythingElse()
    {
        var friend = MakeProfile("prfl_friend", "user-friend");
        var profile = MakeProfile();
        profile.Relationships.Add(new Relationship
        {
            Id = "rlsp_friend", OwnerId = profile.Id, TargetId = friend.Id, Target = friend,
            Status = DomainRelationshipStatus.Friends,
        });

        var minimal = profile.ToIntegrationProfile().ToMinimalIntegrationProfile();

        Assert.Multiple(() =>
        {
            Assert.That(minimal.Id, Is.EqualTo(profile.Id));
            Assert.That(minimal.UserId, Is.EqualTo(profile.UserId));
            Assert.That(minimal.UserName, Is.EqualTo("tester"));
            Assert.That(minimal.AvatarUrl, Is.Not.Null.And.Not.Empty);
            Assert.That(minimal.Bio, Is.Null);
            Assert.That(minimal.AccentColor, Is.Null);
            Assert.That(minimal.Relationships, Is.Empty);
        });
    }
}
