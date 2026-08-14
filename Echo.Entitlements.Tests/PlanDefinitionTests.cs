using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;

namespace Echo.Entitlements.Tests;

/// <summary>Plans come from configuration and nothing in the library knows a tier number.</summary>
[TestFixture]
public class PlanDefinitionTests
{
    [Test]
    public void A_plan_carries_whatever_numbers_configuration_gave_it()
    {
        var pro = TierFixtures.Catalogue().Find(TierFixtures.ProGuild);

        Assert.That(pro, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(pro!.Values[EntitlementKeys.VoiceMaxParticipants].AsNumber, Is.EqualTo(50));
            Assert.That(pro.Values[EntitlementKeys.GuildVanityUrl].AsFlag, Is.True);
            Assert.That(EntitlementKeys.VoiceVideoCeiling.Format(pro.Values[EntitlementKeys.VoiceVideoCeiling]),
                Is.EqualTo("1080p60"));
            Assert.That(pro.Values[EntitlementKeys.GuildBotsInstalled].IsUnlimited, Is.True,
                "'unlimited' is a value a plan can hold, not an absence of one");
        });
    }

    [Test]
    public void Plan_lookup_is_case_insensitive_and_misses_return_null()
    {
        var catalogue = TierFixtures.Catalogue();

        Assert.Multiple(() =>
        {
            Assert.That(catalogue.Find("PRO"), Is.Not.Null);
            Assert.That(catalogue.Find("enterprise"), Is.Null, "an undefined plan is not an error");
            Assert.That(catalogue.Find(null), Is.Null);
        });
    }

    [Test]
    public void A_plan_naming_a_key_that_does_not_exist_fails_at_load()
    {
        var options = TierFixtures.Options();
        options.Plans[TierFixtures.ProGuild]["guild.emoji_slotz"] = "500";

        Assert.That(() => PlanCatalogue.FromOptions(options), Throws.InstanceOf<KeyNotFoundException>(),
            "a typo'd key would otherwise be a plan that quietly grants one fewer thing than intended");
    }

    [Test]
    public void A_plan_value_of_the_wrong_shape_fails_at_load()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => Load("guild.vanity_url", "500"), Throws.InstanceOf<FormatException>());
            Assert.That(() => Load("voice.max_participants", "lots"), Throws.InstanceOf<FormatException>());
            Assert.That(() => Load("voice.max_participants", "-1"), Throws.InstanceOf<FormatException>());
            Assert.That(() => Load("voice.video_ceiling", "4k120"), Throws.InstanceOf<ArgumentException>(),
                "a rung that is not on the ladder has no rank, so there is nothing to compare it with");
        });
    }

    [Test]
    public void An_empty_configuration_is_a_valid_state()
    {
        var catalogue = PlanCatalogue.FromOptions(new EntitlementPlanOptions());

        Assert.That(catalogue.Plans, Is.Empty,
            "a deployment that has defined no plans resolves everything to today's behaviour, which "
            + "is what lets the resolver ship before any pricing decision");
    }

    [Test]
    public async Task The_plan_source_puts_the_assigned_plans_values_on_the_subject()
    {
        var resolver = new EntitlementResolver(
        [
            new PlanDefaultEntitlementSource(
                TierFixtures.Catalogue(),
                new ScriptedPlanAssignment(TierFixtures.PlusGuild, TierFixtures.VentaPlus)),
        ]);

        var guild = await resolver.ResolveAsync(Subjects.Guild);
        var user = await resolver.ResolveAsync(Subjects.User);

        Assert.Multiple(() =>
        {
            Assert.That(guild.Number(EntitlementKeys.VoiceMaxParticipants), Is.EqualTo(25));
            Assert.That(guild.ProvenanceOf(EntitlementKeys.VoiceMaxParticipants).Source,
                Is.EqualTo(EntitlementPrecedence.PlanDefault));
            Assert.That(guild.ProvenanceOf(EntitlementKeys.VoiceMaxParticipants).Detail,
                Is.EqualTo(TierFixtures.PlusGuild),
                "the provenance names the plan, so support can see which one was applied");
            Assert.That(user.Number(EntitlementKeys.UserMaxDevices), Is.EqualTo(10));
        });
    }

    [Test]
    public async Task A_guild_plan_that_mentions_a_user_key_is_ignored_rather_than_trusted()
    {
        var options = TierFixtures.Options();
        options.Plans[TierFixtures.ProGuild]["user.max_devices"] = "99";

        var resolver = new EntitlementResolver(
        [
            new PlanDefaultEntitlementSource(
                PlanCatalogue.FromOptions(options),
                new ScriptedPlanAssignment(TierFixtures.ProGuild, TierFixtures.FreeUser)),
        ]);

        var guild = await resolver.ResolveAsync(Subjects.Guild);
        var user = await resolver.ResolveAsync(Subjects.User);

        Assert.Multiple(() =>
        {
            Assert.That(guild.Contains(EntitlementKeys.UserMaxDevices), Is.False);
            Assert.That(user.Number(EntitlementKeys.UserMaxDevices), Is.EqualTo(5),
                "the user's own plan decides, not whichever guild they happen to be resolved against");
        });
    }

    [Test]
    public async Task An_unassigned_subject_falls_through_to_the_catalogue_defaults()
    {
        var resolver = new EntitlementResolver(
        [
            new PlanDefaultEntitlementSource(
                TierFixtures.Catalogue(), new ScriptedPlanAssignment(null, null)),
        ]);

        var guild = await resolver.ResolveAsync(Subjects.Guild);

        Assert.That(guild.ProvenanceOf(EntitlementKeys.VoiceMaxParticipants).Source,
            Is.EqualTo(EntitlementPrecedence.CatalogueDefault));
    }

    [Test]
    public async Task The_plan_source_sits_below_every_other_source_and_cannot_lower_them()
    {
        var resolver = new EntitlementResolver(
        [
            new PlanDefaultEntitlementSource(
                TierFixtures.Catalogue(),
                new ScriptedPlanAssignment(TierFixtures.FreeGuild, TierFixtures.FreeUser)),
            StubSource.Returning(EntitlementPrecedence.AdminGrant,
                b => b.Number(EntitlementKeys.VoiceMaxParticipants, 50, "grant-7")),
        ]);

        var guild = await resolver.ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(guild.Number(EntitlementKeys.VoiceMaxParticipants), Is.EqualTo(50));
            Assert.That(guild.ProvenanceOf(EntitlementKeys.VoiceMaxParticipants).Detail, Is.EqualTo("grant-7"));
            Assert.That(guild.Number(EntitlementKeys.GuildEmojiSlots), Is.EqualTo(50),
                "the keys the grant said nothing about still come from the plan");
        });
    }

    [Test]
    public void A_plan_built_in_code_still_has_its_values_shape_checked()
    {
        var mismatched = new Dictionary<EntitlementKey, EntitlementValue>
        {
            [EntitlementKeys.GuildEmojiSlots] = EntitlementValue.OfFlag(true),
        };

        Assert.That(() => new PlanDefinition("broken", mismatched), Throws.InstanceOf<ArgumentException>());
    }

    private static PlanCatalogue Load(string keyName, string value) =>
        PlanCatalogue.FromOptions(new EntitlementPlanOptions
        {
            Plans = new Dictionary<string, Dictionary<string, string>>
            {
                ["custom"] = new() { [keyName] = value },
            },
        });
}
