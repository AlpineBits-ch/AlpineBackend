using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Sources;
using Echo.Voice.Abuse;
using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Echo.Voice.Transport;

namespace Echo.Voice.Tests.Rooms;

/// <summary>Catching a publisher who is sending more than they declared.</summary>
[TestFixture]
public class VoiceOverPublishTests
{
    private const string Guild = "guild-1";
    private const string Publisher = "u01";

    private readonly VoiceRoomKey _key = VoiceRoomKey.Channel("channel-1");

    private VoiceTestHarness.SerializingLockService _locks = null!;
    private VoiceTestHarness _h = null!;

    [SetUp]
    public void SetUp()
    {
        _locks = new VoiceTestHarness.SerializingLockService();
        _h = new VoiceTestHarness(_locks);
    }

    private VoiceRoomService Service(string rung) =>
        new(_h.Rooms, _h.Announcer, null,
            new EntitlementResolver([new GuildRung(rung)]));

    /// <summary>A publisher on the roster with a camera, having declared
    /// <paramref name="declaredHeight"/> lines.</summary>
    private async Task PublishAsync(VoiceRoomService service, int declaredHeight)
    {
        await service.JoinAsync(_key, Publisher, "device-1", Guild);
        await service.RecordPublishAsync(_key, Publisher, Publisher);
        await service.RecordTracksAsync(
            _key, Publisher, Publisher, ["camera"], null, declaredHeight);
    }

    /// <summary>What the SFU reports: one participant sending <paramref name="height"/> lines.</summary>
    private static IReadOnlyList<VoiceSfuParticipant> Sending(int height) =>
    [
        new(Publisher, Publisher, true, ["audio", "camera"],
        [
            new VoiceSfuTrack("audio", "TR_a", false, 0),
            new VoiceSfuTrack("camera", "TR_v", true, height),
        ]),
    ];

    private List<CapturedSend> Capped() =>
        _h.SendsOf("guild.voice." + VoiceEvents.PublishCapped);

    private static IReadOnlyList<object?> DegradationsOf(CapturedSend send) =>
        (send.Envelope!["degradations"] as IEnumerable<object?>)?.ToList() ?? [];

    // ── The catch ─────────────────────────────────────────────────────────────

    [Test]
    public async Task A_publisher_above_their_rung_is_reported()
    {
        var service = Service("720p30");
        await PublishAsync(service, 720);

        var findings = await service.DetectOverPublishAsync(_key, Sending(1080));

        Assert.Multiple(() =>
        {
            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].UserId, Is.EqualTo(Publisher));
            Assert.That(findings[0].ObservedHeight, Is.EqualTo(1080));
            Assert.That(findings[0].GrantedRung, Is.EqualTo("720p30"));
            Assert.That(findings[0].GuildId, Is.EqualTo(Guild));
            Assert.That(findings[0].TrackName, Is.EqualTo("camera"));
        });
    }

    /// <summary>The distinction the whole event exists to carry.</summary>
    [Test]
    public async Task A_declaration_that_does_not_describe_the_publisher_is_distinguishable()
    {
        var lying = Service("720p30");
        await PublishAsync(lying, 720);
        var lied = await lying.DetectOverPublishAsync(_key, Sending(1080));

        SetUp();
        var honest = Service("720p30");
        await PublishAsync(honest, 1080);
        var declared = await honest.DetectOverPublishAsync(_key, Sending(1080));

        Assert.Multiple(() =>
        {
            Assert.That(lied[0].DeclaredHeight, Is.EqualTo(720));
            Assert.That(lied[0].DeclarationMatchesReality, Is.False,
                "they said 720 and the SFU sees 1080 - no misconfiguration explains that on its own");

            Assert.That(declared[0].DeclaredHeight, Is.EqualTo(1080));
            Assert.That(declared[0].DeclarationMatchesReality, Is.True,
                "an honest client above its plan is a billing question, not a client question");
        });
    }

    [Test]
    public async Task The_overshoot_is_expressed_as_a_multiple_of_what_was_permitted()
    {
        var service = Service("720p30");
        await PublishAsync(service, 720);

        var findings = await service.DetectOverPublishAsync(_key, Sending(2160));

        Assert.That(findings[0].Overshoot(720), Is.EqualTo(3).Within(0.01),
            "an encoder overshooting sits near 1; something else entirely does not");
    }

    // ── What is renderable ────────────────────────────────────────────────────

    [Test]
    public async Task The_publisher_is_told_in_the_ordinary_degradation_vocabulary()
    {
        var service = Service("720p30");
        await PublishAsync(service, 720);

        await service.DetectOverPublishAsync(_key, Sending(1080), instanceSellsUpgrades: true);

        var capped = Capped();
        Assert.Multiple(() =>
        {
            Assert.That(capped, Has.Count.EqualTo(1));
            Assert.That(capped[0].Target, Does.Contain(Publisher),
                "it is about their own encoder and nobody else's business");
            Assert.That(DegradationsOf(capped[0]), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task Nobody_else_in_the_room_is_told()
    {
        var service = Service("720p30");
        await PublishAsync(service, 720);
        await service.JoinAsync(_key, "u02", "device-2", Guild);
        _h.ClearSends();

        await service.DetectOverPublishAsync(_key, Sending(1080));

        Assert.That(Capped().Any(s => s.Target.Contains("u02")), Is.False);
    }

    // ── Only a change is announced ────────────────────────────────────────────

    /// <summary>The sweep re-measures every minute.</summary>
    [Test]
    public async Task An_unchanged_observation_is_not_re_announced()
    {
        var service = Service("720p30");
        await PublishAsync(service, 720);

        await service.DetectOverPublishAsync(_key, Sending(1080));
        _h.ClearSends();
        var second = await service.DetectOverPublishAsync(_key, Sending(1080));

        Assert.Multiple(() =>
        {
            Assert.That(Capped(), Is.Empty, "nothing moved, so there is nothing to say again");
            Assert.That(second, Is.Empty,
                "and the finding is not re-raised for an observation that has not changed");
        });
    }

    [Test]
    public async Task Coming_back_inside_the_rung_clears_the_banner()
    {
        var service = Service("720p30");
        await PublishAsync(service, 720);
        await service.DetectOverPublishAsync(_key, Sending(1080));
        _h.ClearSends();

        var findings = await service.DetectOverPublishAsync(_key, Sending(720));

        var capped = Capped();
        Assert.Multiple(() =>
        {
            Assert.That(capped, Has.Count.EqualTo(1), "the client is told, or the banner never goes");
            Assert.That(DegradationsOf(capped[0]), Is.Empty,
                "an empty degradation list is how 'you are compliant again' is said");
            Assert.That(findings, Is.Empty, "and compliance is not a finding");
        });
    }

    [Test]
    public async Task A_publisher_who_climbs_further_is_re_announced()
    {
        var service = Service("720p30");
        await PublishAsync(service, 720);
        await service.DetectOverPublishAsync(_key, Sending(1080));
        _h.ClearSends();

        var findings = await service.DetectOverPublishAsync(_key, Sending(2160));

        Assert.Multiple(() =>
        {
            Assert.That(Capped(), Has.Count.EqualTo(1));
            Assert.That(findings.Single().ObservedHeight, Is.EqualTo(2160));
        });
    }

    // ── What must never produce a false positive ──────────────────────────────

    [Test]
    public async Task A_publisher_inside_their_rung_is_left_alone()
    {
        var service = Service("1080p60");
        await PublishAsync(service, 1080);

        var findings = await service.DetectOverPublishAsync(_key, Sending(1080));

        Assert.Multiple(() =>
        {
            Assert.That(findings, Is.Empty);
            Assert.That(Capped(), Is.Empty);
        });
    }

    /// <summary>
    /// A control plane that could not be reached and a room where nobody is publishing look
    /// identical from here.
    /// </summary>
    [Test]
    public async Task An_empty_reading_measures_nothing_rather_than_clearing_everybody()
    {
        var service = Service("720p30");
        await PublishAsync(service, 720);
        await service.DetectOverPublishAsync(_key, Sending(1080));
        _h.ClearSends();

        var findings = await service.DetectOverPublishAsync(_key, []);

        Assert.Multiple(() =>
        {
            Assert.That(findings, Is.Empty);
            Assert.That(Capped(), Is.Empty, "an absence of evidence is not evidence of compliance");
        });
    }

    /// <summary>
    /// The loudest possible false positive: Billing unreachable would otherwise report every
    /// publisher in every room as over their plan, and deliver it as a banner to all of them at once.
    /// </summary>
    [Test]
    public async Task An_unresolvable_ceiling_reports_nobody()
    {
        var service = new VoiceRoomService(
            _h.Rooms, _h.Announcer, null, new EntitlementResolver([new ThrowingSource()]));

        await PublishAsync(service, 720);
        _h.ClearSends();

        var findings = await service.DetectOverPublishAsync(_key, Sending(2160));

        Assert.Multiple(() =>
        {
            Assert.That(findings, Is.Empty);
            Assert.That(Capped(), Is.Empty);
        });
    }

    [Test]
    public async Task Audio_is_never_measured_against_a_video_ceiling()
    {
        var service = Service("none");
        await service.JoinAsync(_key, Publisher, "device-1", Guild);
        await service.RecordPublishAsync(_key, Publisher, Publisher);

        var findings = await service.DetectOverPublishAsync(_key,
        [
            new VoiceSfuParticipant(Publisher, Publisher, true, ["audio"],
                [new VoiceSfuTrack("audio", "TR_a", false, 0)]),
        ]);

        Assert.That(findings, Is.Empty,
            "the most restrictive ceiling expressible must not be able to report a microphone");
    }

    /// <summary>
    /// A desktop client publishes its screen from a second connection under a suffixed identity.
    /// </summary>
    [Test]
    public async Task Video_on_a_secondary_connection_is_measured_against_the_same_user()
    {
        var service = Service("720p30");
        await PublishAsync(service, 720);

        var findings = await service.DetectOverPublishAsync(_key,
        [
            new VoiceSfuParticipant(Publisher, Publisher, true, ["audio"],
                [new VoiceSfuTrack("audio", "TR_a", false, 0)]),
            new VoiceSfuParticipant($"{Publisher}#screen", Publisher, false, ["screen-abc"],
                [new VoiceSfuTrack("screen-abc", "TR_s", true, 2160)]),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].ObservedHeight, Is.EqualTo(2160));
            Assert.That(findings[0].TrackName, Is.EqualTo("screen-abc"));
        });
    }

    [Test]
    public async Task A_participant_the_sfu_does_not_report_is_not_reported()
    {
        var service = Service("720p30");
        await PublishAsync(service, 720);
        await service.JoinAsync(_key, "u02", "device-2", Guild);

        var findings = await service.DetectOverPublishAsync(_key, Sending(1080));

        Assert.That(findings.Select(f => f.UserId), Is.EqualTo(new[] { Publisher }));
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private sealed class GuildRung(string rung) : IEntitlementSource
    {
        public EntitlementPrecedence Precedence => EntitlementPrecedence.Subscription;

        public Task<EntitlementSet> ResolveAsync(EntitlementSubject subject, CancellationToken ct)
        {
            if (subject.Kind != SubjectKind.Guild) return Task.FromResult(EntitlementSet.Empty);

            var builder = new EntitlementSetBuilder(EntitlementPrecedence.Subscription);
            builder.Rung(EntitlementKeys.VoiceVideoCeiling, rung);
            return Task.FromResult(builder.Build());
        }
    }

    private sealed class ThrowingSource : IEntitlementSource
    {
        public EntitlementPrecedence Precedence => EntitlementPrecedence.Subscription;

        public Task<EntitlementSet> ResolveAsync(EntitlementSubject subject, CancellationToken ct) =>
            throw new InvalidOperationException("Billing is unreachable");
    }
}
