using Social.Api.Extensions;
using Social.Domain.Aggregate;
using DomainRelationshipStatus = Social.Domain.Enums.RelationshipStatus;
using ContractRelationshipStatus = Social.Contracts.Dtos.RelationshipStatus;

namespace Social.Tests.Extensions;

[TestFixture]
public class IntegrationProfileDtoExtensionsTests
{
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
            Assert.That(dto.AvatarUrl, Is.EqualTo("https://api.venta.gg/api/v1/social/profiles/prfl_42/avatar"));
            Assert.That(dto.BannerUrl, Is.EqualTo("https://api.venta.gg/api/v1/social/profiles/prfl_42/banner"));
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
}
