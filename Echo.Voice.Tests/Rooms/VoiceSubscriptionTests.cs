using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Echo.Voice.Tests.Usage;
using Echo.Voice.Tracks;
using Echo.Voice.Transport;
using Echo.Voice.Usage;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Echo.Voice.Tests.Rooms;

/// <summary>
/// Subscription planning wired to the real store, service, announcer and reconciler.
/// </summary>
[TestFixture]
public class VoiceSubscriptionTests
{
    private const string Alice = "alice";
    private const string Bob = "bob";

    private static readonly DateTimeOffset Start = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly VoiceRoomKey _key = VoiceRoomKey.Channel("channel-1");

    private VoiceTestHarness.SerializingLockService _locks = null!;
    private VoiceTestHarness _h = null!;
    private FakeClock _clock = null!;
    private FakeVoiceUsageBackend _usageBackend = null!;
    private VoiceUsageMeter _meter = null!;
    private VoiceSubscriptions _subscriptions = null!;
    private VoiceRoomService _service = null!;
    private VoiceReconciler _reconciler = null!;

    /// <summary>Three, so a five-person room is a large one and the tests stay readable.</summary>
    private static readonly VoiceSubscriptionOptions Options = new()
    {
        ActiveSpeakerThreshold = 3,
        ActiveSpeakerCount = 2,
        MaxActiveSpeakers = 3,
        Enforce = true,
    };

    [SetUp]
    public void SetUp()
    {
        _locks = new VoiceTestHarness.SerializingLockService();
        _h = new VoiceTestHarness(_locks);
        _clock = new FakeClock(Start);
        _usageBackend = new FakeVoiceUsageBackend(_clock);
        _meter = new VoiceUsageMeter(_usageBackend, _clock, NullLogger<VoiceUsageMeter>.Instance);
        _subscriptions = NewSubscriptions(Options);
        _service = new VoiceRoomService(_h.Rooms, _h.Announcer, _subscriptions);
        _reconciler = new VoiceReconciler(
            _h.Rooms, _h.Announcer, _h.Cache, NullLogger<VoiceReconciler>.Instance,
            _meter, _subscriptions);
    }

    private VoiceSubscriptions NewSubscriptions(VoiceSubscriptionOptions options) => new(
        new VoiceAttentionStore(_locks, _h.Cache, options), options, _clock,
        NullLogger<VoiceSubscriptions>.Instance);

    private static string Event(string name) => "guild.voice." + name;

    private async Task<VoiceRoom> PopulateAsync(int size)
    {
        for (var i = 0; i < size; i++)
        {
            var userId = $"u{i:D2}";
            await _service.JoinAsync(_key, userId, $"device-{i}", "guild-1");
            await _service.RecordPublishAsync(_key, userId, $"cf-{userId}");
        }
        return (await _h.Rooms.LoadAsync(_key))!;
    }

    private List<CapturedSend> SubscriptionPushes() =>
        _h.SendsOf(Event(VoiceEvents.SubscriptionsChanged));

    // ── Announcing: once per change, never per recomputation ──────────────────

    [Test]
    public async Task Somebody_starting_to_speak_pushes_a_new_set_to_every_participant()
    {
        var room = await PopulateAsync(5);
        _h.ClearSends();

        await _service.SetSpeakingAsync(_key, "u01", true);

        var pushes = SubscriptionPushes();
        Assert.Multiple(() =>
        {
            Assert.That(pushes.Select(p => p.Target).Distinct().ToList(), Has.Count.EqualTo(5),
                "the ranked set is shared, so everybody's set moved and everybody has to be told");
            Assert.That(pushes.Single(p => p.Target == "user:u02").Envelope!["mode"],
                Is.EqualTo(VoiceSubscriptionMode.ActiveSpeaker));
            Assert.That(room.Participants, Has.Count.EqualTo(5));
        });
    }

    [Test]
    public async Task A_recomputation_that_changes_nothing_pushes_nothing()
    {
        await PopulateAsync(5);
        await _service.SetSpeakingAsync(_key, "u01", true);
        _h.ClearSends();

        // The same person keeps talking, and somebody else's mute changes. Neither moves the set.
        await _service.SetSpeakingAsync(_key, "u01", true);
        await _service.SetMuteAsync(_key, "u03", true, serverForced: false);

        Assert.That(SubscriptionPushes(), Is.Empty,
            "a push per recomputation would cost every subscriber in the room an SDP exchange every "
            + "time somebody drew breath");
    }

    [Test]
    public async Task A_small_room_is_never_told_about_subscriptions_at_all()
    {
        await PopulateAsync(3);
        _h.ClearSends();

        await _service.SetSpeakingAsync(_key, "u01", true);

        Assert.That(SubscriptionPushes(), Is.Empty,
            "at or below the threshold the room is all-to-all and there is nothing to say");
    }

    [Test]
    public async Task A_joiner_is_handed_their_subscription_set_with_the_join_snapshot()
    {
        await PopulateAsync(5);
        await _service.SetSpeakingAsync(_key, "u01", true);
        _h.ClearSends();

        await _service.JoinAsync(_key, Alice, "device-a", "guild-1");

        var snapshot = _h.SendsTo(Alice).Single(s => s.Method == Event(VoiceEvents.Snapshot)).Snapshot!;

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Subscriptions, Is.Not.Null,
                "a joiner told to pull nothing until the next recomputation hears nothing");
            Assert.That(snapshot.Subscriptions!.Tracks.Select(t => t.UserId), Does.Contain("u01"));
        });
    }

    [Test]
    public async Task A_small_room_snapshot_carries_no_subscription_field()
    {
        await _service.JoinAsync(_key, Alice, "device-a", "guild-1");

        var snapshot = _h.SendsTo(Alice).Single(s => s.Method == Event(VoiceEvents.Snapshot)).Snapshot!;

        Assert.That(snapshot.Subscriptions, Is.Null,
            "a client seeing the field at all is a client that should honour it, so the case where "
            + "honouring it must mean nothing is the case where it is absent");
    }

    [Test]
    public async Task A_pin_is_told_only_to_the_person_who_made_it()
    {
        await PopulateAsync(5);
        await _service.SetSpeakingAsync(_key, "u01", true);
        _h.ClearSends();

        await _service.SetSubscriberAsync(_key, "u04", new VoiceSubscriberUpdate(Pinned: ["u02"]));

        var pushes = SubscriptionPushes();
        Assert.Multiple(() =>
        {
            Assert.That(pushes.Select(p => p.Target).Distinct().ToList(), Is.EqualTo(new[] { "user:u04" }));
            Assert.That(
                ((IEnumerable<VoiceSubscription>)pushes[0].Envelope!["tracks"]!).Select(t => t.UserId),
                Does.Contain("u02"));
        });
    }

    // ── Metering belief ──────────────────────────────────────────────────────

    /// <summary>
    /// The default posture, and the one that matters most: the plan is advice, and the meter is not
    /// allowed to believe it.
    /// </summary>
    [Test]
    public async Task An_unenforced_plan_is_metered_all_to_all()
    {
        _subscriptions = NewSubscriptions(Options with { Enforce = false });
        _service = new VoiceRoomService(_h.Rooms, _h.Announcer, _subscriptions);
        _reconciler = new VoiceReconciler(
            _h.Rooms, _h.Announcer, _h.Cache, NullLogger<VoiceReconciler>.Instance,
            _meter, _subscriptions);

        await PopulateAsync(5);
        await _service.SetSpeakingAsync(_key, "u01", true);
        var room = (await _h.Rooms.LoadAsync(_key))!;

        await _reconciler.HeartbeatAsync("u00", _key, room.InstanceId, room.Version, "cf-u00", TrackNaming.Audio);
        _clock.Advance(VoiceUsageMeter.SampleInterval);
        room = (await _h.Rooms.LoadAsync(_key))!;
        await _reconciler.HeartbeatAsync("u00", _key, room.InstanceId, room.Version, "cf-u00", TrackNaming.Audio);

        var totals = await _meter.GetDayAsync("guild-1", DateOnly.FromDateTime(_clock.Now.UtcDateTime));
        var interval = (long)VoiceUsageMeter.SampleInterval.TotalSeconds;

        Assert.That(totals.SubscriberSeconds.GetValueOrDefault(VoiceUsageTrackKind.Audio),
            Is.EqualTo(5 * 4 * interval),
            "five publishers pulled by four peers each - the bill the system still has");
    }

    // ── Video publisher cap ───────────────────────────────────────────────────

    [Test]
    public async Task The_video_publisher_cap_admits_existing_publishers_and_refuses_new_ones()
    {
        _subscriptions = NewSubscriptions(Options with { MaxVideoPublishers = 2 });
        _service = new VoiceRoomService(_h.Rooms, _h.Announcer, _subscriptions);

        await PopulateAsync(5);
        await _service.RecordTracksAsync(_key, "u00", "cf-u00", [TrackNaming.ScreenTrack("share-a")]);
        await _service.RecordTracksAsync(_key, "u01", "cf-u01", [TrackNaming.ScreenTrack("share-b")]);

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.CanPublishVideoAsync(_key, "u00"), Is.True,
                "somebody already distributing video is never the one the cap catches");
            Assert.That(await _service.CanPublishVideoAsync(_key, "u02"), Is.False);
        });
    }

    /// <summary>
    /// Camera used to be announced and then forgotten, so it existed in a <c>TrackPublished</c>
    /// event and nowhere else - which meant the usage meter read camera egress as zero, on the more
    /// expensive half of the video bill.
    /// </summary>
    [Test]
    public async Task A_camera_is_recorded_on_the_roster_and_therefore_measurable()
    {
        await PopulateAsync(2);
        await _service.RecordTracksAsync(_key, "u00", "cf-u00", ["camera"]);

        var room = (await _h.Rooms.LoadAsync(_key))!;
        var fanout = VoiceUsageMeter.Measure(room);

        Assert.Multiple(() =>
        {
            Assert.That(room.Find("u00")!.ActiveVideoTracks.Select(v => v.TrackName),
                Is.EqualTo(new[] { "camera" }));
            Assert.That(fanout.Subscribers.GetValueOrDefault(VoiceUsageTrackKind.Camera),
                Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Closing_a_camera_takes_it_off_the_roster()
    {
        await PopulateAsync(2);
        await _service.RecordTracksAsync(_key, "u00", "cf-u00", ["camera"]);
        await _service.RecordTracksClosedAsync(_key, "u00", ["camera"]);

        var room = (await _h.Rooms.LoadAsync(_key))!;

        Assert.Multiple(() =>
        {
            Assert.That(room.Find("u00")!.ActiveVideoTracks, Is.Empty);
            Assert.That(VoiceUsageMeter.Measure(room).Subscribers
                .GetValueOrDefault(VoiceUsageTrackKind.Camera), Is.Zero);
        });
    }

    /// <summary>
    /// Two clients both publishing a track called <c>camera</c> is legal and expected, so a plan
    /// keyed on the name alone would match one person's subscribe against the other's track and
    /// would have the meter count one camera where there are two.
    /// </summary>
    [Test]
    public async Task Two_cameras_with_the_same_track_name_are_told_apart()
    {
        await PopulateAsync(5);
        await _service.SetSpeakingAsync(_key, "u01", true);
        await _service.RecordTracksAsync(_key, "u00", "cf-u00", ["camera"]);
        await _service.RecordTracksAsync(_key, "u01", "cf-u01", ["camera"]);

        var room = (await _h.Rooms.LoadAsync(_key))!;
        var plan = await _service.GetSubscriptionsAsync(_key);
        var fanout = VoiceUsageMeter.Measure(room, plan);

        Assert.Multiple(() =>
        {
            Assert.That(plan.For("u02").Tracks.Count(t => t.TrackName == "camera"), Is.EqualTo(2));
            Assert.That(fanout.Tracks.GetValueOrDefault(VoiceUsageTrackKind.Camera), Is.EqualTo(2));
            Assert.That(fanout.Subscribers.GetValueOrDefault(VoiceUsageTrackKind.Camera),
                Is.EqualTo(8), "two cameras, four subscribers each");
        });
    }

    // ── Idle room reaping ─────────────────────────────────────────────────────

    [Test]
    public async Task The_last_participant_out_closes_the_room()
    {
        await _service.JoinAsync(_key, Alice, "device-a", "guild-1");
        await _service.LeaveAsync(_key, Alice);

        Assert.Multiple(async () =>
        {
            Assert.That(await _h.Rooms.LoadAsync(_key), Is.Null,
                "an empty channel should not keep a roster, an instance id and an attention blob "
                + "for the rest of the afternoon");
            Assert.That(
                await _h.Cache.GetStringAsync(VoiceAttentionStore.CacheKey(_key)), Is.Null);
        });
    }

    [Test]
    public async Task A_room_that_still_has_somebody_in_it_survives_a_leave()
    {
        await _service.JoinAsync(_key, Alice, "device-a", "guild-1");
        await _service.JoinAsync(_key, Bob, "device-b", "guild-1");

        await _service.LeaveAsync(_key, Alice);

        var room = await _h.Rooms.LoadAsync(_key);
        Assert.Multiple(() =>
        {
            Assert.That(room, Is.Not.Null);
            Assert.That(room!.Find(Bob), Is.Not.Null);
        });
    }

    [Test]
    public async Task A_room_whose_every_participant_stopped_heartbeating_is_reaped()
    {
        var room = await PopulateAsync(2);
        foreach (var participant in room.Participants)
            participant.JoinedAt = Start.UtcDateTime - Options.IdleRoomGrace - TimeSpan.FromMinutes(5);
        await VoiceTestHarness.SeedRoomAsync(_h.Cache, room);
        _h.ClearSends();

        var outcome = await _reconciler.ReapAsync(_key);

        Assert.Multiple(async () =>
        {
            Assert.That(outcome, Is.EqualTo(VoiceReapOutcome.Closed));
            Assert.That(await _h.Rooms.LoadAsync(_key), Is.Null);
            Assert.That(_h.SendsOf(Event(VoiceEvents.Resync)).Select(s => s.Envelope!["reason"]),
                Has.All.EqualTo("roomReaped"),
                "one of them may be a live client whose liveness key expired rather than a dead "
                + "one, and a resync costs them a rejoin instead of the call");
        });
    }

    [Test]
    public async Task A_room_with_one_live_heartbeat_is_left_alone()
    {
        var room = await PopulateAsync(2);
        foreach (var participant in room.Participants)
            participant.JoinedAt = Start.UtcDateTime - TimeSpan.FromHours(1);
        await VoiceTestHarness.SeedRoomAsync(_h.Cache, room);

        await _h.Cache.SetStringAsync(VoiceReconciler.LivenessKey("u01"), _key.ToString());

        Assert.Multiple(async () =>
        {
            Assert.That(await _reconciler.ReapAsync(_key), Is.EqualTo(VoiceReapOutcome.Live));
            Assert.That(await _h.Rooms.LoadAsync(_key), Is.Not.Null);
        });
    }

    /// <summary>
    /// Joining does not write a liveness key - only the first heartbeat does - so somebody thirty
    /// seconds into a call is indistinguishable from somebody who is gone unless the grace covers
    /// it. Reaping them would look exactly like being dropped from a channel they just joined.
    /// </summary>
    [Test]
    public async Task A_participant_who_has_not_heartbeated_yet_is_covered_by_the_join_grace()
    {
        await PopulateAsync(2);

        Assert.Multiple(async () =>
        {
            Assert.That(await _reconciler.ReapAsync(_key), Is.EqualTo(VoiceReapOutcome.Live));
            Assert.That(await _h.Rooms.LoadAsync(_key), Is.Not.Null);
        });
    }

    [Test]
    public async Task Reaping_a_room_that_is_already_gone_is_not_an_error()
    {
        Assert.That(await _reconciler.ReapAsync(_key), Is.EqualTo(VoiceReapOutcome.NotFound));
    }

    // ── The meter, which is the whole measurement gate ────────────────────────

    [Test]
    public async Task A_heartbeat_meters_the_planned_fanout_rather_than_the_room_size()
    {
        await PopulateAsync(5);
        await _service.SetSpeakingAsync(_key, "u01", true);
        var room = (await _h.Rooms.LoadAsync(_key))!;

        await _reconciler.HeartbeatAsync("u00", _key, room.InstanceId, room.Version, "cf-u00", TrackNaming.Audio);
        _clock.Advance(VoiceUsageMeter.SampleInterval);
        room = (await _h.Rooms.LoadAsync(_key))!;
        await _reconciler.HeartbeatAsync("u00", _key, room.InstanceId, room.Version, "cf-u00", TrackNaming.Audio);

        var totals = await _meter.GetDayAsync("guild-1", DateOnly.FromDateTime(_clock.Now.UtcDateTime));
        var interval = (long)VoiceUsageMeter.SampleInterval.TotalSeconds;

        Assert.That(totals.SubscriberSeconds.GetValueOrDefault(VoiceUsageTrackKind.Audio),
            Is.EqualTo(4 * interval),
            "one ranked speaker pulled by the other four, not five publishers pulled by four peers "
            + "each - a meter that kept reporting the latter would hide the entire saving");
    }

    /// <summary>
    /// The before-and-after, computed through <c>WP-02</c>'s own arithmetic rather than measured on
    /// a live call.
    /// </summary>
    [Test]
    public void Fifty_people_and_three_talkers_is_an_order_of_magnitude_cheaper()
    {
        var room = new VoiceRoom { RoomId = "room-50", Kind = VoiceRoomKind.Channel, GuildId = "guild-1" };
        for (var i = 0; i < 50; i++)
        {
            room.Participants.Add(new VoiceParticipant
            {
                UserId = $"u{i:D2}",
                MediaSessionId = $"cf-u{i:D2}",
                AudioTrackName = TrackNaming.Audio,
                JoinedAt = Start.UtcDateTime.AddSeconds(i),
            });
        }

        var attention = new VoiceAttention();
        foreach (var userId in new[] { "u07", "u21", "u42" })
        {
            attention.Speaker(userId).IsSpeaking = true;
            attention.Speaker(userId).SpeakingSinceUnixMs = Start.ToUnixTimeMilliseconds();
        }

        var options = VoiceSubscriptionOptions.Default;
        VoiceSubscriptionPlanner.Select(room, attention, options, Start.AddSeconds(5));
        var plan = VoiceSubscriptionPlanner.Build(room, attention, options);

        var before = VoiceUsageMeter.Measure(room, null);
        var after = VoiceUsageMeter.Measure(room, plan);

        const long hour = 3600;
        var beforeBytes = VoiceUsageRates.EgressBytes(
            VoiceUsageTrackKind.Audio, before.TotalSubscribers * hour);
        var afterBytes = VoiceUsageRates.EgressBytes(
            VoiceUsageTrackKind.Audio, after.TotalSubscribers * hour);

        Assert.Multiple(() =>
        {
            Assert.That(before.TotalSubscribers, Is.EqualTo(50L * 49));
            Assert.That(after.TotalSubscribers, Is.EqualTo(47L * 3 + 3 * 2));
            Assert.That(beforeBytes / 1_000_000_000d, Is.EqualTo(35d).Within(1d),
                "monetization.md 1.1 puts the fifty-person audio room at ~35 GB/hour, and a model "
                + "that cannot reproduce that number is not modelling the same thing");
            Assert.That(afterBytes / 1_000_000_000d, Is.LessThan(5d),
                "and the same room under active-speaker subscription at ~4");
            Assert.That((double)before.TotalSubscribers / after.TotalSubscribers,
                Is.GreaterThan(10d));
        });
    }

    /// <summary>
    /// The before-and-after as the meter itself records it, with the enforcement switch in each
    /// position and nothing else changed: same roster, same talkers, same elapsed time, same
    /// heartbeat path, same Redis counters.
    /// </summary>
    [Test]
    public async Task The_switch_moves_what_the_meter_records_for_the_same_room()
    {
        var advisory = await MeterOneRoomAsync("advisory", enforce: false);
        var binding = await MeterOneRoomAsync("binding", enforce: true);

        Assert.Multiple(() =>
        {
            Assert.That(advisory, Is.EqualTo(20L * 19 * (long)VoiceUsageMeter.SampleInterval.TotalSeconds),
                "off means the meter reports all-to-all, because that is what a client is still "
                + "permitted to pull");
            // Seventeen listeners pulling three speakers, and three speakers pulling each other.
            Assert.That(binding, Is.EqualTo((17L * 3 + 3 * 2) * (long)VoiceUsageMeter.SampleInterval.TotalSeconds),
                "on means it reports the plan, which is what the subscribe path will now hold "
                + "clients to");
            Assert.That((double)advisory / binding, Is.GreaterThan(6d));
        });
    }

    /// <summary>Twenty people, three of them talking, sampled across exactly one interval. Returns
    /// the audio subscriber-seconds the meter recorded.</summary>
    private async Task<long> MeterOneRoomAsync(string name, bool enforce)
    {
        var clock = new FakeClock(Start);
        var backend = new FakeVoiceUsageBackend(clock);
        var meter = new VoiceUsageMeter(backend, clock, NullLogger<VoiceUsageMeter>.Instance);
        var subscriptions = new VoiceSubscriptions(
            new VoiceAttentionStore(_locks, _h.Cache, Options with { Enforce = enforce }),
            Options with { Enforce = enforce }, clock, NullLogger<VoiceSubscriptions>.Instance);
        var service = new VoiceRoomService(_h.Rooms, _h.Announcer, subscriptions);
        var reconciler = new VoiceReconciler(
            _h.Rooms, _h.Announcer, _h.Cache, NullLogger<VoiceReconciler>.Instance, meter, subscriptions);

        var key = VoiceRoomKey.Channel($"channel-{name}");
        var scope = $"guild-{name}";

        for (var i = 0; i < 20; i++)
        {
            await service.JoinAsync(key, $"p{i:D2}", $"device-{i}", scope);
            await service.RecordPublishAsync(key, $"p{i:D2}", $"cf-p{i:D2}");
        }

        foreach (var talker in new[] { "p03", "p11", "p17" })
            await service.SetSpeakingAsync(key, talker, true);

        var room = (await _h.Rooms.LoadAsync(key))!;
        await reconciler.HeartbeatAsync("p00", key, room.InstanceId, room.Version, "cf-p00", TrackNaming.Audio);
        clock.Advance(VoiceUsageMeter.SampleInterval);
        room = (await _h.Rooms.LoadAsync(key))!;
        await reconciler.HeartbeatAsync("p00", key, room.InstanceId, room.Version, "cf-p00", TrackNaming.Audio);

        var totals = await meter.GetDayAsync(scope, DateOnly.FromDateTime(clock.Now.UtcDateTime));
        return totals.SubscriberSeconds.GetValueOrDefault(VoiceUsageTrackKind.Audio);
    }

    [Test]
    public void Metering_without_a_plan_is_unchanged()
    {
        var room = new VoiceRoom { RoomId = "room-1", Kind = VoiceRoomKind.Channel, GuildId = "guild-1" };
        for (var i = 0; i < 10; i++)
        {
            room.Participants.Add(new VoiceParticipant
            {
                UserId = $"u{i:D2}",
                MediaSessionId = $"cf-u{i:D2}",
                AudioTrackName = TrackNaming.Audio,
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(VoiceUsageMeter.Measure(room).TotalSubscribers, Is.EqualTo(90L));
            Assert.That(
                VoiceUsageMeter.Measure(room, VoiceSubscriptionPlan.Unplanned).TotalSubscribers,
                Is.EqualTo(90L),
                "a non-selective plan must not change what ninety days of retained counters mean");
        });
    }

    // ── Failure: lose the optimisation, never the call ────────────────────────

    [Test]
    public async Task A_service_with_no_subscription_planning_behaves_exactly_as_before()
    {
        var service = new VoiceRoomService(_h.Rooms, _h.Announcer);

        await service.JoinAsync(_key, Alice, "device-a", "guild-1");
        await service.RecordPublishAsync(_key, Alice, "cf-alice");
        await service.SetSpeakingAsync(_key, Alice, true);

        Assert.Multiple(async () =>
        {
            Assert.That(SubscriptionPushes(), Is.Empty);
            Assert.That(await service.GetSubscriptionsAsync(_key),
                Is.EqualTo(VoiceSubscriptionPlan.Unplanned));
        });
    }
}
