using System.Reflection;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;

namespace Echo.Entitlements.Tests;

/// <summary>The architecture test over the key catalogue.</summary>
[TestFixture]
public class EntitlementCatalogueTests
{
    [Test]
    public void Every_key_declares_a_scope_a_kind_and_a_default()
    {
        Assert.That(EntitlementKeys.All, Is.Not.Empty);

        Assert.Multiple(() =>
        {
            foreach (var key in EntitlementKeys.All)
            {
                Assert.That(key.Name, Is.Not.Null.And.Not.Empty);
                Assert.That(Enum.IsDefined(key.Scope), Is.True, $"'{key.Name}' has no declared scope");
                Assert.That(Enum.IsDefined(key.ValueKind), Is.True, $"'{key.Name}' has no declared value kind");
                Assert.That(key.Default.Kind, Is.EqualTo(key.ValueKind),
                    $"'{key.Name}' declares a default of the wrong shape, so it would fail to merge "
                    + "with anything a source supplied");
            }
        });
    }

    /// <summary>The half that a per-key loop cannot catch.</summary>
    [Test]
    public void Every_declared_key_is_listed_in_the_catalogue()
    {
        var declared = typeof(EntitlementKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(EntitlementKey))
            .Select(field => (EntitlementKey)field.GetValue(null)!)
            .ToList();

        Assert.That(declared, Is.Not.Empty, "reflection found no keys, so this test is checking nothing");
        Assert.That(EntitlementKeys.All, Is.EquivalentTo(declared));
    }

    [Test]
    public void Key_names_are_unique_and_stable_in_shape()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EntitlementKeys.All.Select(key => key.Name).Distinct(StringComparer.Ordinal).Count(),
                Is.EqualTo(EntitlementKeys.All.Count),
                "two keys with one name would resolve into each other's values");

            foreach (var key in EntitlementKeys.All)
            {
                // The name is written into grants, plan configuration and client DTOs, so it is a
                // storage format and a public contract rather than an identifier.
                Assert.That(key.Name, Does.Match("^[a-z][a-z0-9_]*\\.[a-z][a-z0-9_]*$"),
                    $"'{key.Name}' does not follow the area.key convention every other key uses");
            }
        });
    }

    [Test]
    public void Every_ladder_key_carries_its_ladder_and_a_default_on_it()
    {
        Assert.Multiple(() =>
        {
            foreach (var key in EntitlementKeys.All)
            {
                if (key.ValueKind == EntitlementValueKind.Ladder)
                {
                    Assert.That(key.Ladder, Is.Not.Null, $"'{key.Name}' is a ladder with no rungs");
                    Assert.That(key.Default.AsRank,
                        Is.InRange(key.Ladder!.LowestRank, key.Ladder.HighestRank),
                        $"'{key.Name}' defaults to a rung that is not on its own ladder");
                }
                else
                {
                    Assert.That(key.Ladder, Is.Null, $"'{key.Name}' is not a ladder but carries one");
                }
            }
        });
    }

    /// <summary>
    /// The defaults are the floor under the whole mechanism, not the Free tier, and they are set to
    /// today's unenforced behaviour so that shipping the resolver before any plan exists changes
    /// nothing.
    /// </summary>
    [Test]
    public void Numeric_and_paired_defaults_are_permissive_so_an_absent_source_binds_nothing()
    {
        Assert.Multiple(() =>
        {
            foreach (var key in EntitlementKeys.All.Where(k => k.ValueKind == EntitlementValueKind.Numeric))
            {
                Assert.That(key.Default.IsUnlimited, Is.True,
                    $"'{key.Name}' would enforce a limit nobody configured");
            }

            foreach (var key in EntitlementKeys.All.Where(k => k.Scope == EntitlementScope.Paired))
            {
                var top = key.ValueKind == EntitlementValueKind.Ladder
                    ? EntitlementValue.OfRank(key.Ladder!.HighestRank)
                    : EntitlementValue.OfNumber(EntitlementValue.Unlimited);

                Assert.That(key.Default.Raw, Is.EqualTo(top.Raw),
                    $"'{key.Name}' is paired, so a subject with no source on one side must not "
                    + "restrict the other side below what it paid for");
            }
        });
    }

    [Test]
    public void Scope_decides_which_subject_kinds_a_key_applies_to()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EntitlementKeys.VoiceMaxParticipants.AppliesTo(SubjectKind.Guild), Is.True);
            Assert.That(EntitlementKeys.VoiceMaxParticipants.AppliesTo(SubjectKind.User), Is.False);
            Assert.That(EntitlementKeys.UserMaxDevices.AppliesTo(SubjectKind.User), Is.True);
            Assert.That(EntitlementKeys.UserMaxDevices.AppliesTo(SubjectKind.Guild), Is.False);
            Assert.That(EntitlementKeys.VoiceVideoCeiling.AppliesTo(SubjectKind.Guild), Is.True);
            Assert.That(EntitlementKeys.VoiceVideoCeiling.AppliesTo(SubjectKind.User), Is.True,
                "a paired key is exactly the one both sides may set");
        });
    }

    [Test]
    public void An_unknown_key_name_fails_loudly_and_names_the_real_ones()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EntitlementKeys.TryGet("voice.max_particpants", out _), Is.False);
            Assert.That(() => EntitlementKeys.Require("voice.max_particpants"),
                Throws.InstanceOf<KeyNotFoundException>()
                    .With.Message.Contains("voice.max_participants"),
                "a typo in plan configuration is otherwise a key that silently grants nothing");
        });
    }

    [Test]
    public void Keys_compare_by_name_so_a_key_rebuilt_from_a_string_lands_in_the_same_bucket()
    {
        var fromCatalogue = EntitlementKeys.GuildEmojiSlots;
        var rebuilt = EntitlementKeys.Require("guild.emoji_slots");

        var map = new Dictionary<EntitlementKey, int> { [fromCatalogue] = 1 };

        Assert.Multiple(() =>
        {
            Assert.That(rebuilt, Is.EqualTo(fromCatalogue));
            Assert.That(map.ContainsKey(rebuilt), Is.True);
        });
    }

    [Test]
    public void The_video_ladder_orders_its_rungs_lowest_first_and_includes_audio_only()
    {
        var ladder = EntitlementLadders.VideoQuality;

        Assert.Multiple(() =>
        {
            Assert.That(ladder.RankOf("none"), Is.EqualTo(ladder.LowestRank),
                "degrade-not-deny needs a rung that means audio only, or the enforcement site has "
                + "nothing to answer an over-budget room with");
            Assert.That(ladder.RankOf("480p30"), Is.LessThan(ladder.RankOf("720p30")));
            Assert.That(ladder.RankOf("720p30"), Is.LessThan(ladder.RankOf("1080p30")));
            Assert.That(ladder.RankOf("1080p30"), Is.LessThan(ladder.RankOf("1080p60")));
            Assert.That(ladder.RankOf("1080p60"), Is.LessThan(ladder.RankOf("1440p60")));
            Assert.That(ladder.RankOf("1440p60"), Is.LessThan(ladder.RankOf("2160p60")));
            Assert.That(() => ladder.RankOf("4k120"), Throws.InstanceOf<ArgumentException>());
        });
    }

    [Test]
    public void A_ladder_needs_at_least_two_distinct_rungs()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => new EntitlementLadder("solo", "only"), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => new EntitlementLadder("dupes", "a", "A"), Throws.InstanceOf<ArgumentException>(),
                "rung lookup is case-insensitive, so two rungs differing only in case are one rung");
        });
    }
}
