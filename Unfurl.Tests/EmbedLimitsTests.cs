using Bots.Contracts.Gateway.Payloads;
using Messaging.Domain.Previews;

namespace Unfurl.Tests;

/// <summary>The size caps, and the generated/authored split they interact with.</summary>
public class EmbedLimitsTests
{
    [Test]
    public void Clamp_OverlongTitleAndDescription_AreTruncated()
    {
        var embed = new EmbedPayload
        {
            Title = new string('a', 400),
            Description = new string('b', 5000),
        };

        EmbedLimits.Clamp(embed);

        Assert.Multiple(() =>
        {
            Assert.That(embed.Title, Has.Length.EqualTo(EmbedLimits.Title));
            Assert.That(embed.Description, Has.Length.EqualTo(EmbedLimits.Description));
        });
    }

    [Test]
    public void Clamp_ShortValues_AreUntouched()
    {
        var embed = new EmbedPayload { Title = "short", Description = "also short" };

        EmbedLimits.Clamp(embed);

        Assert.Multiple(() =>
        {
            Assert.That(embed.Title, Is.EqualTo("short"));
            Assert.That(embed.Description, Is.EqualTo("also short"));
        });
    }

    [Test]
    public void Clamp_TruncationDoesNotSplitAGrapheme()
    {
        // Cutting at an arbitrary char index can leave half a surrogate pair, which renders as a
        // replacement glyph and which at least one JSON parser rejects outright.
        var embed = new EmbedPayload { Title = string.Concat(Enumerable.Repeat("👩‍👩‍👧‍👦", 100)) };

        EmbedLimits.Clamp(embed);

        Assert.Multiple(() =>
        {
            Assert.That(embed.Title!.Length, Is.LessThanOrEqualTo(EmbedLimits.Title));
            Assert.That(char.IsLowSurrogate(embed.Title[^1]) || !char.IsHighSurrogate(embed.Title[^1]), Is.True,
                "must not end on a dangling high surrogate");
        });
    }

    [Test]
    public void Clamp_TooManyFields_KeepsTheFirst25()
    {
        var embed = new EmbedPayload
        {
            Fields = Enumerable.Range(0, 40)
                .Select(i => new EmbedFieldPayload { Name = $"n{i}", Value = $"v{i}" })
                .ToList(),
        };

        EmbedLimits.Clamp(embed);

        Assert.That(embed.Fields, Has.Count.EqualTo(EmbedLimits.FieldCount));
    }

    [Test]
    public void ClampTotal_WithinBudget_KeepsEverything()
    {
        var embeds = Enumerable.Range(0, 5)
            .Select(i => new EmbedPayload { Title = $"t{i}", Description = new string('x', 100) })
            .ToList();

        Assert.That(EmbedLimits.ClampTotal(embeds), Has.Count.EqualTo(5));
    }

    [Test]
    public void ClampTotal_OverBudget_DropsTrailingEmbedsWhole()
    {
        // Dropping a whole trailing card beats truncating every card proportionally: a description
        // sliced mid-sentence looks broken, a missing fifth preview just looks missing.
        var embeds = Enumerable.Range(0, 5)
            .Select(_ => new EmbedPayload { Description = new string('x', 2000) })
            .ToList();

        var kept = EmbedLimits.ClampTotal(embeds);

        Assert.Multiple(() =>
        {
            Assert.That(kept, Has.Count.LessThan(5));
            Assert.That(kept.Sum(EmbedLimits.CountedLength), Is.LessThanOrEqualTo(EmbedLimits.TotalPerMessage));
            Assert.That(kept.All(e => e.Description!.Length == 2000), Is.True, "survivors are intact, not shaved");
        });
    }

    [Test]
    public void ClampTotal_NoEmbeds_YieldsNone()
    {
        Assert.That(EmbedLimits.ClampTotal([]), Is.Empty);
    }

    // ── Generated vs authored ────────────────────────────────────────────────

    [Test]
    public void Merge_StampsGeneratedEmbedsWithTheFlag()
    {
        var json = GeneratedEmbeds.Merge(null, [new EmbedPayload { Title = "preview" }]);

        var parsed = GeneratedEmbeds.Parse(json);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Has.Count.EqualTo(1));
            Assert.That(parsed[0].IsGenerated, Is.True);
        });
    }

    [Test]
    public void Merge_KeepsAuthorEmbedsAndPutsGeneratedOnesLast()
    {
        // Generated cards go last so a preview arriving a second after send never reorders content
        // the author already saw.
        var authored = GeneratedEmbeds.Serialize([new EmbedPayload { Title = "bot card" }]);

        var merged = GeneratedEmbeds.Parse(
            GeneratedEmbeds.Merge(authored, [new EmbedPayload { Title = "link preview" }]));

        Assert.Multiple(() =>
        {
            Assert.That(merged, Has.Count.EqualTo(2));
            Assert.That(merged[0].Title, Is.EqualTo("bot card"));
            Assert.That(merged[0].IsGenerated, Is.False);
            Assert.That(merged[1].Title, Is.EqualTo("link preview"));
            Assert.That(merged[1].IsGenerated, Is.True);
        });
    }

    [Test]
    public void Merge_Twice_ReplacesTheOldGeneratedOnes()
    {
        // The re-unfurl case: editing a message must swap the card, not accumulate cards.
        var first = GeneratedEmbeds.Merge(null, [new EmbedPayload { Title = "old" }]);
        var second = GeneratedEmbeds.Merge(first, [new EmbedPayload { Title = "new" }]);

        var parsed = GeneratedEmbeds.Parse(second);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Has.Count.EqualTo(1));
            Assert.That(parsed[0].Title, Is.EqualTo("new"));
        });
    }

    [Test]
    public void RemoveGenerated_LeavesAuthorEmbedsAlone()
    {
        // Suppression drops previews but must not destroy a bot's own card - unsuppressing has to
        // bring it back intact.
        var stored = GeneratedEmbeds.Merge(
            GeneratedEmbeds.Serialize([new EmbedPayload { Title = "bot card" }]),
            [new EmbedPayload { Title = "link preview" }]);

        var remaining = GeneratedEmbeds.Parse(GeneratedEmbeds.RemoveGenerated(stored));

        Assert.Multiple(() =>
        {
            Assert.That(remaining, Has.Count.EqualTo(1));
            Assert.That(remaining[0].Title, Is.EqualTo("bot card"));
        });
    }

    [Test]
    public void RemoveGenerated_WithNothingStored_ReturnsAnEmptyArrayNotNull()
    {
        // null is the patch convention for "leave the stored embeds alone" - the opposite of what
        // a caller clearing previews means.
        Assert.That(GeneratedEmbeds.RemoveGenerated(null), Is.EqualTo("[]"));
    }

    [Test]
    public void HasAuthorEmbeds_DistinguishesTheTwoKinds()
    {
        var generatedOnly = GeneratedEmbeds.Merge(null, [new EmbedPayload { Title = "preview" }]);
        var authored = GeneratedEmbeds.Serialize([new EmbedPayload { Title = "bot card" }]);

        Assert.Multiple(() =>
        {
            Assert.That(GeneratedEmbeds.HasAuthorEmbeds(null), Is.False);
            Assert.That(GeneratedEmbeds.HasAuthorEmbeds("[]"), Is.False);
            Assert.That(GeneratedEmbeds.HasAuthorEmbeds(generatedOnly), Is.False);
            Assert.That(GeneratedEmbeds.HasAuthorEmbeds(authored), Is.True);
        });
    }

    [Test]
    public void Parse_CorruptJson_YieldsNothingRatherThanThrowing()
    {
        // A message whose embeds column is unreadable must still be readable as a message.
        Assert.Multiple(() =>
        {
            Assert.That(GeneratedEmbeds.Parse("{not json"), Is.Empty);
            Assert.That(GeneratedEmbeds.Parse("null"), Is.Empty);
            Assert.That(GeneratedEmbeds.Parse(""), Is.Empty);
        });
    }
}
