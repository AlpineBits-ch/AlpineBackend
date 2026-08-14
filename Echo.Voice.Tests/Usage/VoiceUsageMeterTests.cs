using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Echo.Voice.Tracks;
using Echo.Voice.Usage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Echo.Voice.Tests.Usage;

/// <summary>
/// The meter is the only honest input to any pricing decision, which makes "it recorded something"
/// a useless standard for it.
/// </summary>
[TestFixture]
public class VoiceUsageMeterTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private FakeClock _clock = null!;
    private FakeVoiceUsageBackend _backend = null!;
    private VoiceUsageMeter _meter = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new FakeClock(Start);
        _backend = new FakeVoiceUsageBackend(_clock);
        _meter = NewMeter();
    }

    private VoiceUsageMeter NewMeter() =>
        new(_backend, _clock, NullLogger<VoiceUsageMeter>.Instance);

    private DateOnly Today => DateOnly.FromDateTime(_clock.Now.UtcDateTime);

    private static VoiceRoom Room(string guildId, string roomId, int size)
    {
        var room = new VoiceRoom { RoomId = roomId, Kind = VoiceRoomKind.Channel, GuildId = guildId };
        for (var i = 0; i < size; i++)
            room.Participants.Add(new VoiceParticipant { UserId = $"{roomId}-u{i}" });
        return room;
    }

    private static VoiceParticipant Publishing(VoiceParticipant participant)
    {
        participant.MediaSessionId = "session-" + participant.UserId;
        participant.AudioTrackName = TrackNaming.Audio;
        return participant;
    }

    private static VoiceParticipant Sharing(VoiceParticipant participant, string shareId)
    {
        participant.IsStreaming = true;
        participant.ActiveScreenShares.Add(new ActiveScreenShare
        {
            ShareId = shareId,
            TrackNames = [TrackNaming.ScreenTrack(shareId)],
        });
        return participant;
    }

    /// <summary>Takes the room through one whole interval: the first sample only establishes the
    /// mark, so nothing is accrued until the second.</summary>
    private async Task AccrueOneIntervalAsync(VoiceRoom room)
    {
        await _meter.SampleAsync(room);
        _clock.Advance(VoiceUsageMeter.SampleInterval);
        await _meter.SampleAsync(room);
    }

    private static long Seconds(VoiceUsageTotals totals, VoiceUsageTrackKind kind) =>
        totals.SubscriberSeconds.GetValueOrDefault(kind);

    // ── A room nobody is pulling from is free ─────────────────────────────────

    [Test]
    public async Task A_room_with_one_publisher_and_nobody_else_accrues_nothing()
    {
        var room = Room("guild-a", "room-1", size: 1);
        Publishing(room.Participants[0]);

        await AccrueOneIntervalAsync(room);

        Assert.That(_backend.WroteNothing, Is.True,
            "a track nobody can pull generates no egress and must not be billed for");
    }

    [Test]
    public async Task A_room_where_nobody_publishes_accrues_nothing()
    {
        var room = Room("guild-a", "room-1", size: 10);

        await AccrueOneIntervalAsync(room);

        Assert.That(_backend.WroteNothing, Is.True,
            "ten people sitting in a channel with their microphones off cost nothing");
    }

    [Test]
    public void Measure_reports_no_subscribers_for_a_room_of_one()
    {
        var room = Room("guild-a", "room-1", size: 1);
        Sharing(Publishing(room.Participants[0]), "share-1");

        var fanout = VoiceUsageMeter.Measure(room);

        Assert.Multiple(() =>
        {
            Assert.That(fanout.CostsNothing, Is.True);
            Assert.That(fanout.Tracks, Is.Empty,
                "the tracks are live but nobody is pulling them, and it is the pulling that is billed");
        });
    }

    // ── The headline claim: kinds are not interchangeable ─────────────────────

    /// <summary>The claim the whole pricing model rests on.</summary>
    [Test]
    public async Task A_screenshare_costs_dozens_of_times_what_the_same_audio_fanout_costs()
    {
        var audioRoom = Room("guild-audio", "room-audio", size: 10);
        Publishing(audioRoom.Participants[0]);

        var screenRoom = Room("guild-screen", "room-screen", size: 10);
        Sharing(screenRoom.Participants[0], "share-1");

        await _meter.SampleAsync(audioRoom);
        await _meter.SampleAsync(screenRoom);
        _clock.Advance(VoiceUsageMeter.SampleInterval);
        await _meter.SampleAsync(audioRoom);
        await _meter.SampleAsync(screenRoom);

        var audio = await _meter.GetDayAsync("guild-audio", Today);
        var screen = await _meter.GetDayAsync("guild-screen", Today);

        var audioSeconds = Seconds(audio, VoiceUsageTrackKind.Audio);
        var screenSeconds = Seconds(screen, VoiceUsageTrackKind.Screen);
        var ratio = (double)screen.EstimatedEgressBytes / audio.EstimatedEgressBytes;

        Assert.Multiple(() =>
        {
            Assert.That(audioSeconds, Is.EqualTo(screenSeconds),
                "same room size, same duration, same fan-out - so the seconds must match and the "
                + "whole difference in cost has to come from the kind");
            Assert.That(audioSeconds, Is.GreaterThan(0), "the comparison is worthless if neither accrued");
            Assert.That(ratio, Is.GreaterThanOrEqualTo(40d),
                "monetization.md 1.1: video and screenshare cost roughly 40-50x what audio costs, "
                + "and a meter that cannot show that cannot price anything");
            Assert.That(ratio, Is.EqualTo(
                    (double)VoiceUsageRates.ScreenKilobitsPerSecond / VoiceUsageRates.AudioKilobitsPerSecond)
                .Within(0.001),
                "the ratio must come from the declared bitrates and nowhere else");
        });
    }

    [Test]
    public async Task Kinds_are_recorded_separately_rather_than_blended()
    {
        var room = Room("guild-a", "room-1", size: 4);
        Sharing(Publishing(room.Participants[0]), "share-1");
        room.Participants[0].ActiveScreenShares[0].TrackNames.Add(TrackNaming.ScreenAudioTrack("share-1"));

        await AccrueOneIntervalAsync(room);

        var totals = await _meter.GetDayAsync("guild-a", Today);
        var expected = 3 * (long)VoiceUsageMeter.SampleInterval.TotalSeconds;

        Assert.Multiple(() =>
        {
            Assert.That(Seconds(totals, VoiceUsageTrackKind.Audio), Is.EqualTo(expected));
            Assert.That(Seconds(totals, VoiceUsageTrackKind.Screen), Is.EqualTo(expected));
            Assert.That(Seconds(totals, VoiceUsageTrackKind.ScreenAudio), Is.EqualTo(expected));
        });
    }

    [Test]
    public async Task Audio_fanout_is_counted_quadratically()
    {
        var room = Room("guild-a", "room-1", size: 10);
        foreach (var participant in room.Participants) Publishing(participant);

        await AccrueOneIntervalAsync(room);

        var totals = await _meter.GetDayAsync("guild-a", Today);

        // Ten publishers each pulled by the other nine.
        Assert.That(Seconds(totals, VoiceUsageTrackKind.Audio),
            Is.EqualTo(10 * 9 * (long)VoiceUsageMeter.SampleInterval.TotalSeconds));
    }

    // ── Sampling rate ─────────────────────────────────────────────────────────

    [Test]
    public async Task A_room_records_one_interval_however_many_participants_offer_it()
    {
        var room = Room("guild-a", "room-1", size: 10);
        Publishing(room.Participants[0]);

        // Every participant heartbeats independently, so this is one interval of a ten-person room.
        for (var i = 0; i < 10; i++) await _meter.SampleAsync(room);
        _clock.Advance(VoiceUsageMeter.SampleInterval);
        for (var i = 0; i < 10; i++) await _meter.SampleAsync(room);

        var totals = await _meter.GetDayAsync("guild-a", Today);

        Assert.Multiple(() =>
        {
            Assert.That(totals.Samples, Is.EqualTo(1), "twenty heartbeats, two intervals, one accrual");
            Assert.That(Seconds(totals, VoiceUsageTrackKind.Audio),
                Is.EqualTo(9 * (long)VoiceUsageMeter.SampleInterval.TotalSeconds));
        });
    }

    [Test]
    public async Task Real_elapsed_time_is_credited_rather_than_a_fixed_interval()
    {
        var room = Room("guild-a", "room-1", size: 2);
        Publishing(room.Participants[0]);

        await _meter.SampleAsync(room);
        _clock.Advance(TimeSpan.FromSeconds(95));
        await _meter.SampleAsync(room);

        var totals = await _meter.GetDayAsync("guild-a", Today);

        Assert.That(Seconds(totals, VoiceUsageTrackKind.Audio), Is.EqualTo(95),
            "heartbeats are client timers, so crediting a fixed interval would under-report by "
            + "however much they drift");
    }

    [Test]
    public async Task A_gap_longer_than_the_maximum_credits_nothing()
    {
        var room = Room("guild-a", "room-1", size: 2);
        Publishing(room.Participants[0]);

        await _meter.SampleAsync(room);
        _clock.Advance(VoiceUsageMeter.MaxGap + TimeSpan.FromMinutes(30));
        await _meter.SampleAsync(room);

        var totals = await _meter.GetDayAsync("guild-a", Today);

        Assert.That(totals.IsEmpty, Is.True,
            "a pod that was away for half an hour does not know what the room was doing, and must "
            + "not invent half an hour of egress from what it happens to see now");
    }

    // ── Storage: restart, day boundaries, scopes ──────────────────────────────

    [Test]
    public async Task Counters_survive_a_reconciler_restart()
    {
        var room = Room("guild-a", "room-1", size: 5);
        Publishing(room.Participants[0]);

        await AccrueOneIntervalAsync(room);

        // A new pod, a new meter, the same Redis.
        _meter = NewMeter();
        _clock.Advance(VoiceUsageMeter.SampleInterval);
        await _meter.SampleAsync(room);

        var totals = await _meter.GetDayAsync("guild-a", Today);

        Assert.Multiple(() =>
        {
            Assert.That(Seconds(totals, VoiceUsageTrackKind.Audio),
                Is.EqualTo(2 * 4 * (long)VoiceUsageMeter.SampleInterval.TotalSeconds));
            Assert.That(totals.Samples, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task A_sample_after_midnight_lands_in_the_new_day()
    {
        _clock = new FakeClock(new DateTimeOffset(2026, 8, 14, 23, 58, 0, TimeSpan.Zero));
        _backend = new FakeVoiceUsageBackend(_clock);
        _meter = NewMeter();

        var room = Room("guild-a", "room-1", size: 3);
        Publishing(room.Participants[0]);

        await _meter.SampleAsync(room);
        _clock.Advance(VoiceUsageMeter.SampleInterval);
        await _meter.SampleAsync(room);
        _clock.Advance(VoiceUsageMeter.SampleInterval);
        await _meter.SampleAsync(room);

        var before = new DateOnly(2026, 8, 14);
        var after = new DateOnly(2026, 8, 15);
        var expected = 2 * (long)VoiceUsageMeter.SampleInterval.TotalSeconds;

        var firstDay = await _meter.GetDayAsync("guild-a", before);
        var secondDay = await _meter.GetDayAsync("guild-a", after);
        var both = await _meter.GetRangeAsync("guild-a", before, after);

        Assert.Multiple(async () =>
        {
            Assert.That(Seconds(firstDay, VoiceUsageTrackKind.Audio), Is.EqualTo(expected));
            Assert.That(Seconds(secondDay, VoiceUsageTrackKind.Audio), Is.EqualTo(expected));
            Assert.That(Seconds(both, VoiceUsageTrackKind.Audio), Is.EqualTo(2 * expected),
                "a range read must sum the day buckets rather than pick one");
            Assert.That(await _meter.GetActiveScopesAsync(before), Does.Contain("guild-a"));
            Assert.That(await _meter.GetActiveScopesAsync(after), Does.Contain("guild-a"));
        });
    }

    [Test]
    public async Task Direct_call_usage_is_bucketed_away_from_any_guild()
    {
        var call = new VoiceRoom { RoomId = "call-1", Kind = VoiceRoomKind.Call, GuildId = null };
        call.Participants.Add(Publishing(new VoiceParticipant { UserId = "alice" }));
        call.Participants.Add(new VoiceParticipant { UserId = "bob" });

        await AccrueOneIntervalAsync(call);

        var direct = await _meter.GetDayAsync(VoiceUsageMeter.DirectScope, Today);
        var scopes = await _meter.GetActiveScopesAsync(Today);

        Assert.Multiple(() =>
        {
            Assert.That(Seconds(direct, VoiceUsageTrackKind.Audio),
                Is.EqualTo((long)VoiceUsageMeter.SampleInterval.TotalSeconds),
                "a call is the same SFU and the same bill, so it is measured");
            Assert.That(scopes, Is.EqualTo(new[] { VoiceUsageMeter.DirectScope }),
                "but it belongs to no guild and must not land in one");
        });
    }

    [Test]
    public async Task A_scope_with_no_usage_reads_back_empty_rather_than_failing()
    {
        var totals = await _meter.GetDayAsync("guild-nobody-was-in", Today);

        Assert.Multiple(() =>
        {
            Assert.That(totals.IsEmpty, Is.True);
            Assert.That(totals.EstimatedEgressBytes, Is.Zero);
        });
    }

    // ── Failure: lose measurements, never calls ───────────────────────────────

    [Test]
    public async Task A_redis_outage_does_not_propagate_out_of_the_meter()
    {
        var room = Room("guild-a", "room-1", size: 5);
        Publishing(room.Participants[0]);
        _backend.Unreachable = true;

        Assert.DoesNotThrowAsync(() => _meter.SampleAsync(room));

        // And it recovers on its own once Redis is back, with no restart and no repair step.
        _backend.Unreachable = false;
        await AccrueOneIntervalAsync(room);

        var totals = await _meter.GetDayAsync("guild-a", Today);
        Assert.That(Seconds(totals, VoiceUsageTrackKind.Audio), Is.GreaterThan(0));
    }

    [Test]
    public void A_failure_midway_through_a_sample_does_not_propagate_either()
    {
        var room = Room("guild-a", "room-1", size: 5);
        Publishing(room.Participants[0]);
        _backend.FailAccumulate = true;

        Assert.DoesNotThrowAsync(async () =>
        {
            await _meter.SampleAsync(room);
            _clock.Advance(VoiceUsageMeter.SampleInterval);
            await _meter.SampleAsync(room);
        });
    }

    [Test]
    public void A_read_failure_is_surfaced_rather_than_swallowed()
    {
        _backend.Unreachable = true;

        // The opposite policy to the write side, and deliberately so: a console that answered
        // "this guild used nothing" during an outage would be giving the most expensive wrong
        // answer this data can give.
        Assert.ThrowsAsync<InvalidOperationException>(() => _meter.GetDayAsync("guild-a", Today));
    }

    // ── The hook: it is the reconciler that drives all of the above ───────────

    [Test]
    public async Task A_heartbeat_through_the_reconciler_records_usage()
    {
        var harness = new VoiceTestHarness();
        var reconciler = new VoiceReconciler(
            harness.Rooms, harness.Announcer, harness.Cache,
            NullLogger<VoiceReconciler>.Instance, _meter);

        var key = VoiceRoomKey.Channel("channel-1");
        await harness.Service.JoinAsync(key, "alice", "device-1", "guild-a");
        await harness.Service.JoinAsync(key, "bob", "device-2", "guild-a");
        var room = await harness.Service.RecordPublishAsync(key, "alice", "session-1");

        await reconciler.HeartbeatAsync(
            "alice", key, room!.InstanceId, room.Version, "session-1", TrackNaming.Audio);
        _clock.Advance(VoiceUsageMeter.SampleInterval);
        await reconciler.HeartbeatAsync(
            "alice", key, room.InstanceId, room.Version, "session-1", TrackNaming.Audio);

        var totals = await _meter.GetDayAsync("guild-a", Today);

        Assert.That(Seconds(totals, VoiceUsageTrackKind.Audio),
            Is.EqualTo((long)VoiceUsageMeter.SampleInterval.TotalSeconds),
            "one publisher pulled by one peer, for one interval");
    }

    [Test]
    public async Task A_reconciler_without_a_meter_behaves_exactly_as_before()
    {
        var harness = new VoiceTestHarness();
        var key = VoiceRoomKey.Channel("channel-1");
        await harness.Service.JoinAsync(key, "alice", "device-1", "guild-a");
        var room = await harness.Rooms.LoadAsync(key);

        var outcome = await harness.Reconciler.HeartbeatAsync(
            "alice", key, room!.InstanceId, room.Version, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(VoiceReconcileOutcome.InSync));
            Assert.That(_backend.WroteNothing, Is.True, "metering is opt-in, not a hard dependency");
        });
    }
}
