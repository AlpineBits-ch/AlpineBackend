using FluentValidation;
using Guild.Domain.Aggregates;
using Guild.Domain.Enums;

namespace Guild.Tests.Domain;

/// <summary>
/// The thread-shape audit, as assertions. A scene the thread code does not recognise is missing
/// from thread lists, never auto-archives and escapes ManageOwnThreads/ManageAnyThread, and none of
/// that fails loudly - so the shared helper and the rules that read it are pinned here.
/// </summary>
[TestFixture]
public class SceneThreadShapeTests
{
    private const string GuildId = "guild-1";

    [Test]
    public void SceneName_MayContainWhitespace()
    {
        var scene = Channel.Create(new CreateChannelParams
        {
            Name = "The Siege of Blackwater", Description = "", Type = ChannelType.Scene,
            GuildId = GuildId, ParentChannelId = "chan-1", CreatedByUserId = "user-1",
        });

        Assert.That(scene.Name, Is.EqualTo("The Siege of Blackwater"));
    }

    [Test]
    public void OrdinaryChannelName_StillMayNot()
    {
        Assert.Throws<ValidationException>(() => Channel.Create(new CreateChannelParams
        {
            Name = "The Siege of Blackwater", Description = "", Type = ChannelType.Text,
            GuildId = GuildId,
        }));
    }

    [TestCase(ChannelType.Thread, true)]
    [TestCase(ChannelType.Scene, true)]
    [TestCase(ChannelType.Text, false)]
    [TestCase(ChannelType.Forum, false)]
    [TestCase(ChannelType.Media, false)]
    [TestCase(ChannelType.Voice, false)]
    [TestCase(ChannelType.List, false)]
    public void IsThreadShaped_CoversTheThreadVariantsAndNothingElse(ChannelType type, bool expected) =>
        Assert.That(type.IsThreadShaped(), Is.EqualTo(expected));

    /// <summary>The array exists only because EF cannot translate the extension method; the two
    /// drifting apart would silently un-thread scenes in every query but not in any check.</summary>
    [Test]
    public void ThreadShapedArray_AgreesWithThePredicate()
    {
        foreach (var type in Enum.GetValues<ChannelType>())
        {
            Assert.That(ChannelTypeExtensions.ThreadShaped.Contains(type), Is.EqualTo(type.IsThreadShaped()),
                $"{type} disagrees between the array and the predicate");
        }
    }

    /// <summary>A scene is thread-shaped everywhere the thread code looks, but the module that pays
    /// for it is Scenes.</summary>
    [Test]
    public void SceneChannel_BelongsToTheScenesModule()
    {
        Assert.That(GuildFeatureMap.RequiredFeatureFor(ChannelType.Scene), Is.EqualTo(GuildFeatures.Scenes));
        Assert.That(GuildFeatureMap.RequiredFeatureFor(ChannelType.Thread), Is.EqualTo(GuildFeatures.Threads));
    }

    /// <summary>Threads has to ride along: posting in a scene resolves SendMessagesInThreads, which
    /// GuildFeatureMap gives to the Threads module.</summary>
    [Test]
    public void RoleplayPreset_CarriesScenesAndTheThreadsItRestsOn()
    {
        Assert.That(GuildFeaturePresets.Roleplay.HasFlag(GuildFeatures.Scenes), Is.True);
        Assert.That(GuildFeaturePresets.Roleplay.HasFlag(GuildFeatures.Threads), Is.True);
        Assert.That(GuildFeaturePresets.Roleplay.HasFlag(GuildFeatures.Presence), Is.True);
    }

    [Test]
    public void ManageScenes_IsClampedByTheScenesModule()
    {
        Assert.That(
            GuildFeatureMap.IsPermissionAvailable(GuildFeatures.Personas, ModulePermissions.ManageScenes),
            Is.False);

        Assert.That(
            GuildFeatureMap.IsPermissionAvailable(GuildFeatures.Scenes, ModulePermissions.ManageScenes),
            Is.True);
    }
}
