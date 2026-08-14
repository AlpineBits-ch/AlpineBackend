using System.Text.Json;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Sources;
using Echo.Entitlements.Wire;
using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Echo.Voice.Tracks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Echo.Voice.Tests.Rooms;

/// <summary>
/// The three voice entitlements at the two choke points, wired to the real store, service and
/// announcer.
/// </summary>
[TestFixture]
public class VoiceEntitlementTests
{
    private const string Guild = "guild-1";

    private readonly VoiceRoomKey _key = VoiceRoomKey.Channel("channel-1");

    private VoiceTestHarness.SerializingLockService _locks = null!;
    private VoiceTestHarness _h = null!;

    [SetUp]
    public void SetUp()
    {
        _locks = new VoiceTestHarness.SerializingLockService();
        _h = new VoiceTestHarness(_locks);
    }

    // ── Joining: at the limit, over it, and far over it ───────────────────────

    [Test]
    public async Task A_join_that_fills_the_room_exactly_is_not_a_degradation()
    {
        var service = Service(guild: b => b.Number(EntitlementKeys.VoiceMaxParticipants, 3));

        await JoinAsync(service, 2);
        var third = await service.AdmitAsync(_key, "u03", "device-3", Guild);

        Assert.Multiple(() =>
        {
            Assert.That(third.Position, Is.EqualTo(3));
            Assert.That(third.OverCapacity, Is.Null,
                "somebody who asked for exactly their ceiling got what they asked for, and a banner "
                + "there would be noise on every single join");
            Assert.That(third.Describe(instanceSellsUpgrades: true, actorCanManageGuild: true), Is.Empty);
        });
    }

    [Test]
    public async Task The_member_past_the_cap_still_joins_and_is_told_why_they_are_audio_only()
    {
        var service = Service(guild: b => b.Number(EntitlementKeys.VoiceMaxParticipants, 3));

        await JoinAsync(service, 3);
        var fourth = await service.AdmitAsync(_key, "u04", "device-4", Guild);

        var degradation = fourth.OverCapacity!.Describe(
            instanceSellsUpgrades: true, actorCanManageGuild: true);

        Assert.Multiple(() =>
        {
            Assert.That(fourth.Room.Find("u04"), Is.Not.Null,
                "the eleventh member of a full free room joins; what they lose is video, not the call");
            Assert.That(degradation.Key, Is.EqualTo(EntitlementKeys.VoiceMaxParticipants.Name));
            Assert.That(degradation.Requested.Value, Is.EqualTo(4));
            Assert.That(degradation.Granted.Value, Is.EqualTo(3));
            Assert.That(degradation.Reason, Is.EqualTo(EntitlementReasonCodes.GuildPlanLimit));
            Assert.That(degradation.BoundBy, Is.EqualTo(EntitlementBoundBy.Guild));
            Assert.That(degradation.Remedy, Is.EqualTo(EntitlementRemedyCodes.UpgradeGuild));
            Assert.That(degradation.ActorCanRemedy, Is.True);
            Assert.That(degradation.Subject.Id, Is.EqualTo(Guild));
        });
    }

    [Test]
    public async Task Far_over_the_cap_reports_the_room_as_it_stands_rather_than_the_one_person()
    {
        var service = Service(guild: b => b.Number(EntitlementKeys.VoiceMaxParticipants, 3));

        await JoinAsync(service, 7);
        var eighth = await service.AdmitAsync(_key, "u08", "device-8", Guild);

        Assert.Multiple(() =>
        {
            Assert.That(eighth.Room.Participants, Has.Count.EqualTo(8),
                "nothing turns anybody away, however far over the cap the room is");
            Assert.That(eighth.OverCapacity!.Requested.AsNumber, Is.EqualTo(8),
                "a client comparing a headcount of one against a cap of three renders a meter that "
                + "never fills");
        });
    }

    [Test]
    public async Task The_overflow_publishes_no_video_and_the_reason_is_the_capacity_it_exceeded()
    {
        var service = Service(guild: b => b.Number(EntitlementKeys.VoiceMaxParticipants, 3));

        await JoinAsync(service, 3);
        await service.AdmitAsync(_key, "u04", "device-4", Guild);

        var inside = await service.EvaluateVideoPublishAsync(_key, "u03", VoiceVideoRequest.Best);
        var outside = await service.EvaluateVideoPublishAsync(_key, "u04", VoiceVideoRequest.Best);

        Assert.Multiple(() =>
        {
            Assert.That(inside.VideoAllowed, Is.True);
            Assert.That(outside.VideoAllowed, Is.False);
            Assert.That(outside.Rung, Is.EqualTo("none"),
                "'none' is a real rung, and it is how 'this room is over its video budget' is said "
                + "without refusing the call");
            Assert.That(outside.Refusal!.Cause.Reason,
                Is.EqualTo(EntitlementDegradationReason.GuildPlanLimit));
            Assert.That(outside.Refusal.Cause.BoundBy, Is.EqualTo(EntitlementBoundBy.Guild));
        });
    }

    // ── The paired key, in both directions ────────────────────────────────────

    [Test]
    public async Task A_plus_member_in_a_free_guild_publishes_at_the_guilds_ceiling()
    {
        var service = Service(
            guild: b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "720p30"),
            user: b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "1080p60"));

        await JoinAsync(service, 1);
        var decision = await service.EvaluateVideoPublishAsync(
            _key, "u01", new VoiceVideoRequest(1080, 60));

        var degradation = decision.Degradations
            .Single()
            .Describe(instanceSellsUpgrades: true, actorCanManageGuild: false);

        Assert.Multiple(() =>
        {
            Assert.That(decision.VideoAllowed, Is.True);
            Assert.That(decision.Rung, Is.EqualTo("720p30"),
                "taking the higher would let one paying member fan 1080p out to twenty people in a "
                + "guild that pays nothing");
            Assert.That(decision.Height, Is.EqualTo(720));
            Assert.That(decision.Framerate, Is.EqualTo(30));
            Assert.That(degradation.Reason, Is.EqualTo(EntitlementReasonCodes.PairedCeiling));
            Assert.That(degradation.BoundBy, Is.EqualTo(EntitlementBoundBy.Guild));
            Assert.That(degradation.Remedy, Is.EqualTo(EntitlementRemedyCodes.UpgradeGuild));
            Assert.That(degradation.ActorCanRemedy, Is.False,
                "a member without ManageGuild cannot buy the server's upgrade, and a buy button that "
                + "403s is worse than a sentence");
        });
    }

    [Test]
    public async Task A_free_member_in_a_pro_guild_publishes_at_their_own_ceiling()
    {
        var service = Service(
            guild: b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "1080p60"),
            user: b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "480p30"));

        await JoinAsync(service, 1);
        var decision = await service.EvaluateVideoPublishAsync(
            _key, "u01", new VoiceVideoRequest(1080, 60));

        var degradation = decision.Degradations
            .Single()
            .Describe(instanceSellsUpgrades: true, actorCanManageGuild: true);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Rung, Is.EqualTo("480p30"));
            Assert.That(degradation.Reason, Is.EqualTo(EntitlementReasonCodes.PairedCeiling));
            Assert.That(degradation.BoundBy, Is.EqualTo(EntitlementBoundBy.User),
                "without the side, this is where a paying member is told their own plan limited them");
            Assert.That(degradation.Remedy, Is.EqualTo(EntitlementRemedyCodes.UpgradeUser));
            Assert.That(degradation.Subject.Kind, Is.EqualTo(EntitlementSubjectKinds.User));
        });
    }

    [Test]
    public async Task A_request_inside_both_ceilings_is_not_reported_as_reduced()
    {
        var service = Service(
            guild: b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "1080p30"),
            user: b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "1080p60"));

        await JoinAsync(service, 1);
        var decision = await service.EvaluateVideoPublishAsync(
            _key, "u01", new VoiceVideoRequest(720, 30));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Degradations, Is.Empty);
            Assert.That(decision.Height, Is.EqualTo(720));
            Assert.That(decision.Rung, Is.EqualTo("1080p30"),
                "the ceiling is what they may send, not what they asked to send");
        });
    }

    [Test]
    public async Task A_direct_call_carries_no_guild_ceiling_at_all()
    {
        var call = VoiceRoomKey.Call("call-1");
        var service = Service(
            guild: b => b.Number(EntitlementKeys.VoiceMaxParticipants, 1)
                .Rung(EntitlementKeys.VoiceVideoCeiling, "480p30"));

        await service.AdmitAsync(call, "u01", "device-1");
        var admission = await service.AdmitAsync(call, "u02", "device-2");
        var decision = await service.EvaluateVideoPublishAsync(call, "u02", VoiceVideoRequest.Best);

        Assert.Multiple(() =>
        {
            Assert.That(admission.OverCapacity, Is.Null,
                "there is no guild plan for a two-person call to be charged against");
            Assert.That(decision.Rung, Is.EqualTo("1080p60"));
        });
    }

    // ── Operator ceilings: below wins, above changes nothing ──────────────────

    [Test]
    public async Task An_operator_ceiling_below_the_entitlement_wins_and_offers_no_remedy()
    {
        var service = Service(
            guild: b => b.Number(EntitlementKeys.VoiceMaxParticipants, 50),
            ceilings: OperatorCeilings.Parse(new Dictionary<string, string?>
            {
                [EntitlementKeys.VoiceMaxParticipants.Name] = "2",
            }));

        await JoinAsync(service, 2);
        var third = await service.AdmitAsync(_key, "u03", "device-3", Guild);

        var degradation = third.OverCapacity!.Describe(
            instanceSellsUpgrades: true, actorCanManageGuild: true);

        Assert.Multiple(() =>
        {
            Assert.That(third.Limits.MaxParticipants, Is.EqualTo(2));
            Assert.That(degradation.Reason, Is.EqualTo(EntitlementReasonCodes.OperatorCeiling));
            Assert.That(degradation.BoundBy, Is.Null,
                "an operator ceiling is not a subject anybody can upgrade");
            Assert.That(degradation.Remedy, Is.EqualTo(EntitlementRemedyCodes.None));
            Assert.That(degradation.ActorCanRemedy, Is.False,
                "an upgrade link against a limit the instance's own operator set sells a change that "
                + "would not happen");
        });
    }

    [Test]
    public async Task An_operator_ceiling_above_the_entitlement_does_not_raise_it()
    {
        var service = Service(
            guild: b => b.Number(EntitlementKeys.VoiceMaxParticipants, 3)
                .Rung(EntitlementKeys.VoiceVideoCeiling, "480p30"),
            ceilings: OperatorCeilings.Parse(new Dictionary<string, string?>
            {
                [EntitlementKeys.VoiceMaxParticipants.Name] = "50",
                [EntitlementKeys.VoiceVideoCeiling.Name] = "1080p60",
            }));

        await JoinAsync(service, 3);
        var fourth = await service.AdmitAsync(_key, "u04", "device-4", Guild);

        Assert.Multiple(() =>
        {
            Assert.That(fourth.Limits.MaxParticipants, Is.EqualTo(3),
                "a ceiling above an entitlement is not a grant; treating it as one would make an env "
                + "var a way to buy Pro");
            Assert.That(fourth.Limits.VideoCeiling, Is.EqualTo("480p30"));
            Assert.That(fourth.OverCapacity!.Cause.Reason,
                Is.EqualTo(EntitlementDegradationReason.GuildPlanLimit));
        });
    }

    [Test]
    public async Task An_operator_video_ceiling_below_the_pair_takes_the_reason_with_it()
    {
        var service = Service(
            guild: b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "1080p60"),
            user: b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "1080p60"),
            ceilings: OperatorCeilings.Parse(new Dictionary<string, string?>
            {
                [EntitlementKeys.VoiceVideoCeiling.Name] = "480p30",
            }));

        await JoinAsync(service, 1);
        var decision = await service.EvaluateVideoPublishAsync(
            _key, "u01", new VoiceVideoRequest(1080, 60));

        var degradation = decision.Degradations
            .Single()
            .Describe(instanceSellsUpgrades: true, actorCanManageGuild: true);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Rung, Is.EqualTo("480p30"));
            Assert.That(degradation.Reason, Is.EqualTo(EntitlementReasonCodes.OperatorCeiling));
            Assert.That(degradation.Remedy, Is.EqualTo(EntitlementRemedyCodes.None));
        });
    }

    // ── The publisher cap: the config one and the entitlement one compose ─────

    [Test]
    public async Task The_entitlement_publisher_cap_binds_below_the_configured_one()
    {
        var service = Service(
            guild: b => b.Number(EntitlementKeys.VoiceMaxPublishers, 1),
            options: new VoiceSubscriptionOptions { MaxVideoPublishers = 8 });

        await JoinAsync(service, 3);
        await service.RecordTracksAsync(_key, "u01", "cf-u01", [TrackNaming.ScreenTrack("share-a")]);

        var decision = await service.EvaluateVideoPublishAsync(_key, "u02", VoiceVideoRequest.Best);
        var denial = decision.Denial(instanceSellsUpgrades: true, actorCanManageGuild: true)!;

        Assert.Multiple(async () =>
        {
            Assert.That(decision.Limits.MaxPublishers, Is.EqualTo(1));
            Assert.That(decision.VideoAllowed, Is.False);
            Assert.That(denial.Code, Is.EqualTo(denial.Reason),
                "one lookup table in the client serves refusals and degradations both");
            Assert.That(denial.Key, Is.EqualTo(EntitlementKeys.VoiceMaxPublishers.Name));
            Assert.That(denial.Reason, Is.EqualTo(EntitlementReasonCodes.GuildPlanLimit));
            Assert.That(denial.Requested!.Value, Is.EqualTo(2));
            Assert.That(denial.Granted!.Value, Is.EqualTo(1));
            Assert.That(denial.Retryable, Is.False);
            Assert.That(EntitlementDenialDto.StatusCode, Is.EqualTo(403),
                "never 429, which the rate-limit interceptor retries and swallows, and never 401");
            Assert.That(await service.CanPublishVideoAsync(_key, "u01"), Is.True,
                "somebody already distributing video is never the one the cap catches");
        });
    }

    [Test]
    public async Task The_configured_publisher_cap_binds_below_the_entitlement_one()
    {
        var service = Service(
            guild: b => b.Number(EntitlementKeys.VoiceMaxPublishers, 8),
            options: new VoiceSubscriptionOptions { MaxVideoPublishers = 1 });

        await JoinAsync(service, 3);
        await service.RecordTracksAsync(_key, "u01", "cf-u01", [TrackNaming.ScreenTrack("share-a")]);

        var decision = await service.EvaluateVideoPublishAsync(_key, "u02", VoiceVideoRequest.Best);

        Assert.Multiple(async () =>
        {
            Assert.That(decision.Limits.MaxPublishers, Is.EqualTo(1),
                "the configured cap is what a self-hoster sets, and an entitlement must not raise it");
            Assert.That(decision.Refusal!.Cause.Reason,
                Is.EqualTo(EntitlementDegradationReason.OperatorCeiling));
            Assert.That(await service.CanPublishVideoAsync(_key, "u02"), Is.False);
        });
    }

    [Test]
    public async Task An_unwired_resolver_leaves_the_configured_cap_exactly_as_it_was()
    {
        var options = new VoiceSubscriptionOptions { MaxVideoPublishers = 2 };
        var service = new VoiceRoomService(_h.Rooms, _h.Announcer, Subscriptions(options));

        await JoinAsync(service, 3);
        await service.RecordTracksAsync(_key, "u01", "cf-u01", [TrackNaming.ScreenTrack("share-a")]);
        await service.RecordTracksAsync(_key, "u02", "cf-u02", [TrackNaming.ScreenTrack("share-b")]);

        Assert.Multiple(async () =>
        {
            Assert.That(await service.CanPublishVideoAsync(_key, "u03"), Is.False);
            Assert.That(await service.CanPublishVideoAsync(_key, "u01"), Is.True);
        });
    }

    // ── The limits reaching the client, on the snapshot and its version ───────

    [Test]
    public async Task The_join_snapshot_carries_the_rooms_limits()
    {
        var service = Service(
            guild: b => b.Number(EntitlementKeys.VoiceMaxParticipants, 10)
                .Number(EntitlementKeys.VoiceMaxPublishers, 2)
                .Rung(EntitlementKeys.VoiceVideoCeiling, "720p30"),
            options: new VoiceSubscriptionOptions { MaxVideoPublishers = 8 });

        await JoinAsync(service, 1);
        await service.RecordTracksAsync(_key, "u01", "cf-u01", [TrackNaming.ScreenTrack("share-a")]);
        _h.ClearSends();

        await service.AdmitAsync(_key, "u02", "device-2", Guild);

        var limits = _h.SendsTo("u02")
            .Select(send => send.Snapshot)
            .Last(snapshot => snapshot is not null)!
            .Limits!;

        Assert.Multiple(() =>
        {
            Assert.That(limits.MaxParticipants.Value, Is.EqualTo(10));
            Assert.That(limits.MaxPublishers.Value, Is.EqualTo(2));
            Assert.That(limits.VideoCeiling.Rung, Is.EqualTo("720p30"));
            Assert.That(limits.VideoCeiling.Ladder, Is.EqualTo("video_quality"));
            Assert.That(limits.PublisherCount, Is.EqualTo(1),
                "'2 of 2 people are sharing' is a sentence; 'you cannot share' is a mystery");
        });
    }

    [Test]
    public async Task An_unlimited_ceiling_never_crosses_the_wire_as_a_number()
    {
        var service = Service();

        await JoinAsync(service, 1);

        var limits = _h.SendsTo("u01")
            .Select(send => send.Snapshot)
            .Last(snapshot => snapshot is not null)!
            .Limits!;

        var json = JsonSerializer.Serialize(limits);

        Assert.Multiple(() =>
        {
            Assert.That(limits.MaxParticipants.Unlimited, Is.True);
            Assert.That(limits.MaxParticipants.Value, Is.Null);
            Assert.That(json, Does.Not.Contain(EntitlementValue.Unlimited.ToString()),
                "long.MaxValue exceeds JavaScript's Number.MAX_SAFE_INTEGER and every JS client would "
                + "silently read a different number");
        });
    }

    [Test]
    public async Task A_room_whose_limits_moved_advances_its_version()
    {
        var plan = new MutablePlan { MaxParticipants = 10 };
        var service = new VoiceRoomService(
            _h.Rooms, _h.Announcer, null, new EntitlementResolver([plan]));

        await JoinAsync(service, 1);
        var before = (await _h.Rooms.LoadAsync(_key))!.Version;

        plan.MaxParticipants = 3;
        var after = await service.RefreshLimitsAsync(_key);

        Assert.Multiple(() =>
        {
            Assert.That(after!.Version, Is.GreaterThan(before),
                "a client with gap detection has to learn about this through the counter it already "
                + "watches, not through a second unordered channel it cannot order against it");
            Assert.That(after.Limits!.MaxParticipants, Is.EqualTo(3));
            Assert.That(_h.SendsOf("guild.voice." + VoiceEvents.Resync), Is.Not.Empty);
        });
    }

    [Test]
    public async Task A_refresh_that_changes_nothing_writes_nothing()
    {
        var plan = new MutablePlan { MaxParticipants = 10 };
        var service = new VoiceRoomService(
            _h.Rooms, _h.Announcer, null, new EntitlementResolver([plan]));

        await JoinAsync(service, 1);
        var before = (await _h.Rooms.LoadAsync(_key))!.Version;

        var after = await service.RefreshLimitsAsync(_key);

        Assert.That(after!.Version, Is.EqualTo(before),
            "a version bump for a room that has not changed costs every participant a refetch");
    }

    [Test]
    public async Task A_room_that_has_never_been_resolved_carries_no_limits_block()
    {
        var room = new VoiceRoom { RoomId = "channel-9", Kind = VoiceRoomKind.Channel };

        Assert.That(VoiceRoomSnapshot.From(room).Limits, Is.Null,
            "absent means 'no limit information', which is not the same claim as 'no limits'");
    }

    // ── The resolver is never load-bearing for a call ─────────────────────────

    [Test]
    public async Task A_resolver_that_cannot_answer_does_not_fail_the_join()
    {
        var plan = new MutablePlan { MaxParticipants = 3, Broken = true };
        var service = new VoiceRoomService(
            _h.Rooms, _h.Announcer, null, new EntitlementResolver([plan]));

        var admission = await service.AdmitAsync(_key, "u01", "device-1", Guild);

        Assert.Multiple(() =>
        {
            Assert.That(admission.Room.Find("u01"), Is.Not.Null,
                "a billing outage that mutes everyone's voice chat is a worse incident than a few "
                + "hours of unpaid Pro");
            Assert.That(admission.OverCapacity, Is.Null);
            Assert.That(admission.Room.Limits, Is.Null,
                "and the room is not told it has no limits either, which would be a claim rather than "
                + "an absence");
        });
    }

    [Test]
    public async Task A_room_keeps_the_limits_it_had_when_the_resolver_stops_answering()
    {
        var plan = new MutablePlan { MaxParticipants = 3 };
        var service = new VoiceRoomService(
            _h.Rooms, _h.Announcer, null, new EntitlementResolver([plan]));

        await JoinAsync(service, 1);
        var before = (await _h.Rooms.LoadAsync(_key))!.Version;

        plan.Broken = true;
        var admission = await service.AdmitAsync(_key, "u02", "device-2", Guild);
        var refreshed = await service.RefreshLimitsAsync(_key);

        Assert.Multiple(() =>
        {
            Assert.That(admission.Room.Limits!.MaxParticipants, Is.EqualTo(3),
                "an outage must not flap every room's ceilings to unlimited and back");
            Assert.That(refreshed!.Version, Is.EqualTo(before + 1),
                "the join advanced the version once; the failed refresh advanced it not at all");
        });
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private VoiceSubscriptions Subscriptions(VoiceSubscriptionOptions options) => new(
        new VoiceAttentionStore(_locks, _h.Cache, options), options, TimeProvider.System,
        NullLogger<VoiceSubscriptions>.Instance);

    private VoiceRoomService Service(
        Action<EntitlementSetBuilder>? guild = null,
        Action<EntitlementSetBuilder>? user = null,
        OperatorCeilings? ceilings = null,
        VoiceSubscriptionOptions? options = null) =>
        new(_h.Rooms,
            _h.Announcer,
            options is null ? null : Subscriptions(options),
            new EntitlementResolver([new ScriptedEntitlements(guild, user)]),
            ceilings);

    /// <summary>Joins <paramref name="count"/> people in order.</summary>
    private async Task JoinAsync(VoiceRoomService service, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            await service.JoinAsync(_key, $"u{i:D2}", $"device-{i}", Guild);
        }
    }

    /// <summary>A source that says what a test told it to, per subject kind - which is how a real
    /// guild plan source and a real Venta Plus source behave.</summary>
    private sealed class ScriptedEntitlements(
        Action<EntitlementSetBuilder>? guild, Action<EntitlementSetBuilder>? user) : IEntitlementSource
    {
        public EntitlementPrecedence Precedence => EntitlementPrecedence.Subscription;

        public Task<EntitlementSet> ResolveAsync(
            EntitlementSubject subject, CancellationToken cancellationToken)
        {
            var build = subject.Kind == SubjectKind.Guild ? guild : user;
            if (build is null) return Task.FromResult(EntitlementSet.Empty);

            var builder = new EntitlementSetBuilder(EntitlementPrecedence.Subscription);
            build(builder);
            return Task.FromResult(builder.Build());
        }
    }

    /// <summary>A guild plan that can change while a call is in progress, which is the whole reason
    /// the limits ride the versioned snapshot.</summary>
    private sealed class MutablePlan : IEntitlementSource
    {
        public long MaxParticipants { get; set; }

        /// <summary>Billing unreachable, which is a state every source will eventually be in.</summary>
        public bool Broken { get; set; }

        public EntitlementPrecedence Precedence => EntitlementPrecedence.Subscription;

        public Task<EntitlementSet> ResolveAsync(
            EntitlementSubject subject, CancellationToken cancellationToken)
        {
            if (Broken) throw new InvalidOperationException("billing is unreachable");

            return Task.FromResult(subject.Kind != SubjectKind.Guild
                ? EntitlementSet.Empty
                : new EntitlementSetBuilder(EntitlementPrecedence.Subscription)
                    .Number(EntitlementKeys.VoiceMaxParticipants, MaxParticipants)
                    .Build());
        }
    }
}
