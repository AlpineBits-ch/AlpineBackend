using Echo.Billing;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;

namespace Echo.Tests.Billing;

/// <summary>
/// The provenance screen, which per monetization.md section 6 is worth more than the grant UI
/// itself: without it every billing ticket is "it says I have Pro but it does not work" and there
/// is no reply.
/// </summary>
[TestFixture]
[Category("Unit")]
public class EntitlementProvenanceTests
{
    private sealed class FixedSource(EntitlementPrecedence precedence, EntitlementSet set) : IEntitlementSource
    {
        public EntitlementPrecedence Precedence => precedence;

        public Task<EntitlementSet> ResolveAsync(EntitlementSubject subject, CancellationToken cancellationToken) =>
            Task.FromResult(set);
    }

    private static IEntitlementSource Source(EntitlementPrecedence precedence, long participants, string? detail = null) =>
        new FixedSource(precedence, new EntitlementSetBuilder(precedence)
            .Number(EntitlementKeys.VoiceMaxParticipants, participants, detail)
            .Build());

    private static async Task<IReadOnlyList<EntitlementProvenanceEntryDto>> ResolveAsync(
        params IEntitlementSource[] sources)
    {
        var set = await new EntitlementResolver(sources)
            .ResolveAsync(EntitlementSubject.ForGuild("guild_test"));

        return EntitlementKeys.For(SubjectKind.Guild)
            .Select(key => EntitlementProvenanceEntryDto.From(key, set))
            .ToList();
    }

    private static EntitlementProvenanceEntryDto Participants(
        IReadOnlyList<EntitlementProvenanceEntryDto> entries) =>
        entries.Single(entry => entry.Key == EntitlementKeys.VoiceMaxParticipants.Name);

    /// <summary>The case the screen exists for.</summary>
    [Test]
    public async Task Three_overlapping_sources_credit_the_one_that_supplied_the_winning_value()
    {
        var entries = await ResolveAsync(
            Source(EntitlementPrecedence.AdminGrant, 25, "gnt_support"),
            Source(EntitlementPrecedence.Subscription, 100, "sub_stripe"),
            Source(EntitlementPrecedence.PlanDefault, 10, "free"));

        var participants = Participants(entries);

        Assert.That(participants.Value, Is.EqualTo("100"));
        Assert.That(participants.Source, Is.EqualTo(nameof(EntitlementPrecedence.Subscription)));
        Assert.That(participants.Detail, Is.EqualTo("sub_stripe"));
        Assert.That(participants.IsCatalogueDefault, Is.False);
    }

    /// <summary>The other half of the same rule.</summary>
    [Test]
    public async Task The_highest_standing_source_keeps_the_credit_when_it_also_wins()
    {
        var entries = await ResolveAsync(
            Source(EntitlementPrecedence.AdminGrant, 250, "gnt_support"),
            Source(EntitlementPrecedence.Subscription, 100, "sub_stripe"),
            Source(EntitlementPrecedence.PlanDefault, 10, "free"));

        var participants = Participants(entries);

        Assert.That(participants.Value, Is.EqualTo("250"));
        Assert.That(participants.Source, Is.EqualTo(nameof(EntitlementPrecedence.AdminGrant)));
    }

    /// <summary>A tie is credited upwards.</summary>
    [Test]
    public async Task A_tie_is_credited_to_the_higher_standing_source()
    {
        var entries = await ResolveAsync(
            Source(EntitlementPrecedence.AdminGrant, 100, "gnt_support"),
            Source(EntitlementPrecedence.Subscription, 100, "sub_stripe"),
            Source(EntitlementPrecedence.PlanDefault, 100, "pro"));

        Assert.That(Participants(entries).Source, Is.EqualTo(nameof(EntitlementPrecedence.AdminGrant)));
    }

    /// <summary>A key nobody supplied is still a row, credited to nobody.</summary>
    [Test]
    public async Task A_key_no_source_supplied_is_credited_to_the_catalogue()
    {
        var entries = await ResolveAsync(Source(EntitlementPrecedence.PlanDefault, 10, "free"));
        var vanity = entries.Single(entry => entry.Key == EntitlementKeys.GuildVanityUrl.Name);

        Assert.That(vanity.Source, Is.EqualTo(nameof(EntitlementPrecedence.CatalogueDefault)));
        Assert.That(vanity.IsCatalogueDefault, Is.True);
        Assert.That(vanity.Value, Is.EqualTo(vanity.Default));
    }

    /// <summary>Values are rendered the way plan configuration and grants write them, not as the
    /// long they are carried as. An operator comparing a resolved ceiling against a plan has to be
    /// comparing the same notation, and long.MaxValue beside "unlimited" is two ways of writing one
    /// number that nobody should have to recognise.</summary>
    [Test]
    public async Task An_absent_ceiling_reads_as_unlimited_rather_than_as_a_very_large_number()
    {
        var entries = await ResolveAsync();
        var quota = entries.Single(entry => entry.Key == EntitlementKeys.StorageGuildQuotaBytes.Name);

        Assert.That(quota.Value, Is.EqualTo("unlimited"));
    }

    /// <summary>A ladder reads as its rung name.</summary>
    [Test]
    public async Task A_ladder_key_reads_as_its_rung()
    {
        var entries = await ResolveAsync();
        var ceiling = entries.Single(entry => entry.Key == EntitlementKeys.VoiceVideoCeiling.Name);

        Assert.That(ceiling.ValueKind, Is.EqualTo("Ladder"));
        Assert.That(EntitlementLadders.VideoQuality.Rungs, Does.Contain(ceiling.Value));
    }

    /// <summary>A guild is not shown user keys.</summary>
    [Test]
    public async Task A_guild_is_not_shown_user_scoped_keys()
    {
        var entries = await ResolveAsync();

        Assert.That(entries.Select(entry => entry.Key),
            Does.Not.Contain(EntitlementKeys.UserMaxDevices.Name));

        // The paired ones are the guild's side of the pair and do belong here.
        Assert.That(entries.Select(entry => entry.Key),
            Does.Contain(EntitlementKeys.VoiceVideoCeiling.Name));
    }
}
