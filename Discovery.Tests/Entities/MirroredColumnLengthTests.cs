using Discovery.Domain.Entities;
using Discovery.Tests.Helpers;

namespace Discovery.Tests.Entities;

/// <summary>
/// Discovery references Social's and Guild's Contracts projects, not their Infrastructure, so it
/// cannot compare its column lengths against the source model directly. This pins the next best
/// thing: a mirrored string property must carry no max length at all, since any cap here can only
/// ever be narrower than a source we cannot see and verify against.
/// </summary>
[TestFixture]
public class MirroredColumnLengthTests
{
    [TestCase(typeof(GameTopic), nameof(GameTopic.Name))]
    [TestCase(typeof(GameTopic), nameof(GameTopic.Aliases))]
    [TestCase(typeof(GameTopic), nameof(GameTopic.SteamAppId))]
    [TestCase(typeof(GameTopic), nameof(GameTopic.SearchText))]
    [TestCase(typeof(GuildProfile), nameof(GuildProfile.Name))]
    [TestCase(typeof(GuildProfile), nameof(GuildProfile.IconUrl))]
    [TestCase(typeof(GuildProfile), nameof(GuildProfile.BannerUrl))]
    [TestCase(typeof(GuildProfile), nameof(GuildProfile.Features))]
    public void A_mirrored_property_has_no_max_length(Type entityType, string propertyName)
    {
        using var ctx = TestDiscoveryContext.New();
        var property = ctx.Model.FindEntityType(entityType)!.FindProperty(propertyName)!;
        Assert.That(property.GetMaxLength(), Is.Null);
    }
}
