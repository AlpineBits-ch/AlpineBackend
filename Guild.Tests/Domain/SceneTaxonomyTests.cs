using FluentValidation;
using Guild.Domain.Entity;
using Guild.Domain.Enums;

namespace Guild.Tests.Domain;

/// <summary>
/// The archive's two labels and the end date the archive sorts on. Depth is not covered here: it
/// needs the proposed parent's own parent, so it is the endpoint's check rather than a validator's.
/// </summary>
[TestFixture]
public class SceneTaxonomyTests
{
    private const string GuildId = "guild-1";

    [Test]
    public void Folder_RejectsANamePastTheCap()
    {
        Assert.Throws<ValidationException>(() => SceneFolder.Create(new CreateSceneFolderParams
        {
            GuildId = GuildId, Name = new string('x', SceneFolder.MaxNameLength + 1),
        }));
    }

    [Test]
    public void Folder_TreatsABlankParentAsRoot()
    {
        var folder = SceneFolder.Create(new CreateSceneFolderParams
        {
            GuildId = GuildId, Name = "Arc I", ParentFolderId = "  ",
        });

        Assert.That(folder.ParentFolderId, Is.Null);
    }

    [Test]
    public void Folder_TakesAnIdWithTheArchivePrefix()
    {
        var folder = SceneFolder.Create(new CreateSceneFolderParams { GuildId = GuildId, Name = "Arc I" });

        Assert.That(folder.Id, Does.StartWith("scfd"));
    }

    [Test]
    public void Tag_DefaultsToTheNoColourSentinel()
    {
        var tag = SceneTag.Create(new CreateSceneTagParams { GuildId = GuildId, Name = "betrayal" });

        Assert.That(tag.Color, Is.EqualTo(SceneTag.DefaultColor));
    }

    [Test]
    public void Tag_KeepsOneEmojiAtATime()
    {
        var tag = SceneTag.Create(new CreateSceneTagParams
        {
            GuildId = GuildId, Name = "betrayal", EmojiName = "\U0001F5E1",
        });

        tag.Update(new SceneTag.UpdateSceneTagParams { EmojiId = "emj-1" });

        Assert.Multiple(() =>
        {
            Assert.That(tag.EmojiId, Is.EqualTo("emj-1"));
            Assert.That(tag.EmojiName, Is.Null);
        });
    }

    [Test]
    public void Tag_RejectsBothEmojiKindsAtCreation()
    {
        Assert.Throws<ValidationException>(() => SceneTag.Create(new CreateSceneTagParams
        {
            GuildId = GuildId, Name = "betrayal", EmojiId = "emj-1", EmojiName = "\U0001F5E1",
        }));
    }

    [Test]
    public void Conclude_StampsTheEndDate()
    {
        var scene = SceneState.Create(new CreateSceneStateParams { ChannelId = "chan-1", GuildId = GuildId });
        var now = DateTimeOffset.UtcNow;

        scene.Conclude("And so the gate held.", now);

        Assert.Multiple(() =>
        {
            Assert.That(scene.Status, Is.EqualTo(SceneStatus.Concluded));
            Assert.That(scene.ConcludedAt, Is.EqualTo(now));
            Assert.That(scene.CurrentTurnPersonaId, Is.Null);
            Assert.That(scene.TurnDeadlineAt, Is.Null);
        });
    }

    [Test]
    public void Conclude_KeepsTheFirstEndDateWhenTheNoteIsEditedLater()
    {
        var scene = SceneState.Create(new CreateSceneStateParams { ChannelId = "chan-1", GuildId = GuildId });
        var first = DateTimeOffset.UtcNow;
        scene.Conclude(null, first);

        scene.Conclude("a better closing line", first.AddDays(3));

        Assert.Multiple(() =>
        {
            Assert.That(scene.ConcludedAt, Is.EqualTo(first));
            Assert.That(scene.ConclusionNote, Is.EqualTo("a better closing line"));
        });
    }
}
