using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;

namespace Echo.Entitlements.Tests;

/// <summary>The paired rule: <c>min(guild ceiling, user ceiling)</c>.</summary>
[TestFixture]
public class PairedEntitlementTests
{
    [Test]
    public async Task A_plus_member_in_a_free_guild_publishes_at_the_guilds_ceiling()
    {
        var resolver = Resolver(guildRung: "720p30", userRung: "1080p60");

        var effective = await resolver.ResolveEffectiveAsync(Subjects.Guild, Subjects.User);

        Assert.Multiple(() =>
        {
            Assert.That(effective.Rung(EntitlementKeys.VoiceVideoCeiling), Is.EqualTo("720p30"),
                "the guild pays to distribute it, so the guild's ceiling binds");
            Assert.That(effective.ProvenanceOf(EntitlementKeys.VoiceVideoCeiling).Detail,
                Is.EqualTo("guild-plan"),
                "the provenance screen has to name the side that actually bound");
        });
    }

    [Test]
    public async Task A_free_member_in_a_pro_guild_publishes_at_their_own_ceiling()
    {
        var resolver = Resolver(guildRung: "1080p60", userRung: "720p30");

        var effective = await resolver.ResolveEffectiveAsync(Subjects.Guild, Subjects.User);

        Assert.Multiple(() =>
        {
            Assert.That(effective.Rung(EntitlementKeys.VoiceVideoCeiling), Is.EqualTo("720p30"),
                "the same rule from the other direction - a generous guild does not upgrade the member");
            Assert.That(effective.ProvenanceOf(EntitlementKeys.VoiceVideoCeiling).Detail,
                Is.EqualTo("user-plan"));
        });
    }

    [Test]
    public async Task Equal_ceilings_bind_at_that_value_and_are_credited_to_the_guild()
    {
        var resolver = Resolver(guildRung: "1080p30", userRung: "1080p30");

        var effective = await resolver.ResolveEffectiveAsync(Subjects.Guild, Subjects.User);

        Assert.Multiple(() =>
        {
            Assert.That(effective.Rung(EntitlementKeys.VoiceVideoCeiling), Is.EqualTo("1080p30"));
            Assert.That(effective.ProvenanceOf(EntitlementKeys.VoiceVideoCeiling).Detail,
                Is.EqualTo("guild-plan"),
                "on a tie the useful thing to point at is the side an upgrade would come from");
        });
    }

    [Test]
    public async Task A_member_with_no_source_at_all_does_not_clamp_the_guild()
    {
        var resolver = new EntitlementResolver(
        [
            StubSource.ForKind(EntitlementPrecedence.PlanDefault, SubjectKind.Guild,
                b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "1080p30", "guild-plan")),
        ]);

        var effective = await resolver.ResolveEffectiveAsync(Subjects.Guild, Subjects.User);

        Assert.That(effective.Rung(EntitlementKeys.VoiceVideoCeiling), Is.EqualTo("1080p30"),
            "an absent ceiling is no ceiling. If the user default were a Free number instead, every "
            + "member without a subscription would silently downgrade the guild that paid.");
    }

    [Test]
    public async Task A_guild_with_no_source_at_all_does_not_clamp_the_member()
    {
        var resolver = new EntitlementResolver(
        [
            StubSource.ForKind(EntitlementPrecedence.Subscription, SubjectKind.User,
                b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "480p30", "user-plan")),
        ]);

        var effective = await resolver.ResolveEffectiveAsync(Subjects.Guild, Subjects.User);

        Assert.That(effective.Rung(EntitlementKeys.VoiceVideoCeiling), Is.EqualTo("480p30"));
    }

    [Test]
    public async Task Paired_numeric_ceilings_take_the_lower_in_both_directions()
    {
        var lowGuild = await Upload(guildLimit: 26_214_400, userLimit: 524_288_000);
        var lowUser = await Upload(guildLimit: 524_288_000, userLimit: 26_214_400);

        Assert.Multiple(() =>
        {
            Assert.That(lowGuild, Is.EqualTo(26_214_400));
            Assert.That(lowUser, Is.EqualTo(26_214_400));
        });
    }

    /// <summary>The mistake this method signature exists to prevent.</summary>
    [Test]
    public void Resolving_a_pair_with_the_two_sides_swapped_is_refused()
    {
        var resolver = new EntitlementResolver([]);

        Assert.Multiple(() =>
        {
            Assert.That(async () => await resolver.ResolveEffectiveAsync(Subjects.User, Subjects.Guild),
                Throws.InstanceOf<ArgumentException>());
            Assert.That(async () => await resolver.ResolveEffectiveAsync(Subjects.Guild, Subjects.Guild),
                Throws.InstanceOf<ArgumentException>());
            Assert.That(async () => await resolver.ResolveEffectiveAsync(Subjects.User, Subjects.User),
                Throws.InstanceOf<ArgumentException>());
        });
    }

    [Test]
    public async Task An_effective_set_takes_unpaired_keys_from_their_own_side_only()
    {
        var resolver = new EntitlementResolver(
        [
            StubSource.ForKind(EntitlementPrecedence.PlanDefault, SubjectKind.Guild,
                b => b.Number(EntitlementKeys.VoiceMaxParticipants, 25)),
            StubSource.ForKind(EntitlementPrecedence.Subscription, SubjectKind.User,
                b => b.Number(EntitlementKeys.UserMaxDevices, 10)),
        ]);

        var effective = await resolver.ResolveEffectiveAsync(Subjects.Guild, Subjects.User);

        Assert.Multiple(() =>
        {
            Assert.That(effective.Number(EntitlementKeys.VoiceMaxParticipants), Is.EqualTo(25),
                "a guild key is the guild's answer, not the lower of two");
            Assert.That(effective.Number(EntitlementKeys.UserMaxDevices), Is.EqualTo(10));
            Assert.That(effective.Entries.Select(e => e.Key), Is.EquivalentTo(EntitlementKeys.All),
                "the effective set is what the member may do in the guild, so it spans both scopes");
        });
    }

    [Test]
    public async Task The_bottom_rung_is_a_real_answer_rather_than_a_refusal()
    {
        var resolver = Resolver(guildRung: "none", userRung: "1080p60");
        var effective = await resolver.ResolveEffectiveAsync(Subjects.Guild, Subjects.User);

        Assert.That(effective.Rung(EntitlementKeys.VoiceVideoCeiling), Is.EqualTo("none"),
            "the lowest rung is a real answer - an over-budget guild gets an audio-only room rather "
            + "than a refused join");
    }

    private static EntitlementResolver Resolver(string guildRung, string userRung) =>
        new(
        [
            StubSource.ForKind(EntitlementPrecedence.PlanDefault, SubjectKind.Guild,
                b => b.Rung(EntitlementKeys.VoiceVideoCeiling, guildRung, "guild-plan")),
            StubSource.ForKind(EntitlementPrecedence.Subscription, SubjectKind.User,
                b => b.Rung(EntitlementKeys.VoiceVideoCeiling, userRung, "user-plan")),
        ]);

    private static async Task<long> Upload(long guildLimit, long userLimit)
    {
        var resolver = new EntitlementResolver(
        [
            StubSource.ForKind(EntitlementPrecedence.PlanDefault, SubjectKind.Guild,
                b => b.Number(EntitlementKeys.StorageUploadMaxBytes, guildLimit)),
            StubSource.ForKind(EntitlementPrecedence.Subscription, SubjectKind.User,
                b => b.Number(EntitlementKeys.StorageUploadMaxBytes, userLimit)),
        ]);

        var effective = await resolver.ResolveEffectiveAsync(Subjects.Guild, Subjects.User);
        return effective.Number(EntitlementKeys.StorageUploadMaxBytes);
    }
}
