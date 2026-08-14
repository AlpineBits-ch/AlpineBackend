using Echo.Voice.Rooms;
using Echo.Voice.Tracks;

namespace Echo.Voice.Tests.Rooms;

/// <summary>
/// The ranking and the subscription-set arithmetic, with no storage, no clock and no announcer in
/// the picture.
/// </summary>
[TestFixture]
public class VoiceSubscriptionPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly VoiceSubscriptionOptions Options = new()
    {
        ActiveSpeakerThreshold = 10,
        ActiveSpeakerCount = 5,
        MaxActiveSpeakers = 8,
        MaxPinnedPerSubscriber = 3,
    };

    // ── Builders ──────────────────────────────────────────────────────────────

    private static VoiceRoom Room(int size, bool publishing = true)
    {
        var room = new VoiceRoom { RoomId = "room-1", Kind = VoiceRoomKind.Channel, GuildId = "guild-1" };
        for (var i = 0; i < size; i++)
        {
            var participant = new VoiceParticipant
            {
                UserId = $"u{i:D2}",
                // Explicit and increasing, so every tie-break in the planner is deterministic
                // rather than dependent on how fast the test built the list.
                JoinedAt = Now.UtcDateTime.AddSeconds(i),
            };
            if (publishing)
            {
                participant.MediaSessionId = $"cf-{participant.UserId}";
                participant.AudioTrackName = TrackNaming.Audio;
            }
            room.Participants.Add(participant);
        }
        return room;
    }

    private static VoiceAttention SpeakingNow(VoiceAttention attention, DateTimeOffset since, params string[] userIds)
    {
        foreach (var userId in userIds)
        {
            var speaker = attention.Speaker(userId);
            speaker.IsSpeaking = true;
            speaker.SpeakingSinceUnixMs = since.ToUnixTimeMilliseconds();
        }
        return attention;
    }

    private static VoiceAttention SpokeAt(VoiceAttention attention, DateTimeOffset at, params string[] userIds)
    {
        foreach (var userId in userIds)
        {
            var speaker = attention.Speaker(userId);
            speaker.IsSpeaking = false;
            speaker.SpeakingSinceUnixMs = 0;
            speaker.LastSpokeAtUnixMs = at.ToUnixTimeMilliseconds();
        }
        return attention;
    }

    private static VoiceSubscriptionPlan Plan(
        VoiceRoom room, VoiceAttention attention, DateTimeOffset now, VoiceSubscriptionOptions? options = null)
    {
        options ??= Options;
        VoiceSubscriptionPlanner.Select(room, attention, options, now);
        return VoiceSubscriptionPlanner.Build(room, attention, options);
    }

    private static List<string> AudiblePublishers(VoiceSubscriptionPlan plan, string subscriber) =>
        plan.For(subscriber).Tracks
            .Where(t => t.Kind == TrackNaming.AudioKind)
            .Select(t => t.UserId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    // ── Below the threshold nothing changes ───────────────────────────────────

    [Test]
    public void At_the_threshold_the_room_is_still_all_to_all()
    {
        var room = Room(Options.ActiveSpeakerThreshold);
        var attention = SpeakingNow(new VoiceAttention(), Now.AddSeconds(-3), "u00");

        var plan = Plan(room, attention, Now);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Mode, Is.EqualTo(VoiceSubscriptionMode.All));
            Assert.That(plan.IsSelective, Is.False);
            Assert.That(plan.TotalSubscriptions,
                Is.EqualTo((long)room.Participants.Count * (room.Participants.Count - 1)),
                "the quadratic is not worth managing at this size, and the renegotiation cost of "
                + "managing it is real");
        });
    }

    [Test]
    public void A_small_room_subscribes_everyone_to_everyone_else_but_not_to_themselves()
    {
        var room = Room(4);
        var plan = Plan(room, new VoiceAttention(), Now);

        Assert.Multiple(() =>
        {
            Assert.That(AudiblePublishers(plan, "u00"), Is.EqualTo(new[] { "u01", "u02", "u03" }));
            Assert.That(plan.For("u00").Tracks.Any(t => t.UserId == "u00"), Is.False);
        });
    }

    // ── The headline case ─────────────────────────────────────────────────────

    /// <summary>
    /// The change the whole programme is built around, asserted as a set rather than as a saving.
    /// </summary>
    [Test]
    public void A_room_of_fifty_with_three_talkers_subscribes_to_the_three()
    {
        var room = Room(50);
        var attention = SpeakingNow(new VoiceAttention(), Now.AddSeconds(-3), "u07", "u21", "u42");

        var plan = Plan(room, attention, Now);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Mode, Is.EqualTo(VoiceSubscriptionMode.ActiveSpeaker));
            Assert.That(plan.ActiveSpeakers.OrderBy(id => id, StringComparer.Ordinal),
                Is.EqualTo(new[] { "u07", "u21", "u42" }));
            Assert.That(AudiblePublishers(plan, "u00"), Is.EqualTo(new[] { "u07", "u21", "u42" }),
                "a listener hears the talkers and nobody else");
            Assert.That(AudiblePublishers(plan, "u07"), Is.EqualTo(new[] { "u21", "u42" }),
                "and a talker hears the other two, never themselves");
            Assert.That(plan.TotalSubscriptions, Is.EqualTo(47 * 3 + 3 * 2));
        });
    }

    [Test]
    public void Nobody_is_subscribed_to_a_participant_who_has_never_spoken()
    {
        var room = Room(30);
        var attention = SpeakingNow(new VoiceAttention(), Now.AddSeconds(-3), "u01");

        var plan = Plan(room, attention, Now);

        Assert.That(plan.ActiveSpeakers, Is.EqualTo(new[] { "u01" }),
            "filling the remaining slots with silent participants would cost the bill everything "
            + "the change was meant to save and buy the subscriber nothing");
    }

    /// <summary>The safety guard.</summary>
    [Test]
    public void A_large_room_with_no_recorded_speech_stays_all_to_all()
    {
        var room = Room(40);

        var plan = Plan(room, new VoiceAttention(), Now);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Mode, Is.EqualTo(VoiceSubscriptionMode.All));
            Assert.That(AudiblePublishers(plan, "u00"), Has.Count.EqualTo(39));
        });
    }

    [Test]
    public void Only_publishers_are_ranked()
    {
        var room = Room(20, publishing: false);
        room.Participants[3].MediaSessionId = "cf-u03";
        room.Participants[3].AudioTrackName = TrackNaming.Audio;

        var attention = SpeakingNow(new VoiceAttention(), Now.AddSeconds(-3), "u03", "u09");

        var plan = Plan(room, attention, Now);

        Assert.That(plan.ActiveSpeakers, Is.EqualTo(new[] { "u03" }),
            "a participant with no track is not pullable, so ranking them would hand every "
            + "subscriber a pair the SFU has nothing behind");
    }

    // ── Pins ──────────────────────────────────────────────────────────────────

    [Test]
    public void A_pinned_participant_survives_ranking()
    {
        var room = Room(30);
        var attention = SpeakingNow(new VoiceAttention(), Now.AddSeconds(-3), "u01", "u02", "u03");
        attention.Subscriber("u00").Pinned = ["u25"];

        var plan = Plan(room, attention, Now);

        Assert.Multiple(() =>
        {
            Assert.That(AudiblePublishers(plan, "u00"),
                Is.EqualTo(new[] { "u01", "u02", "u03", "u25" }),
                "a pin is additive to the ranked set, not a competitor for a slot");
            Assert.That(plan.ActiveSpeakers, Does.Not.Contain("u25"),
                "and it is one subscriber's choice, so it must not reach anybody else's set");
            Assert.That(AudiblePublishers(plan, "u10"), Is.EqualTo(new[] { "u01", "u02", "u03" }));
        });
    }

    [Test]
    public void Pins_are_capped_so_a_client_cannot_restore_the_quadratic_on_its_own()
    {
        var room = Room(30);
        var attention = SpeakingNow(new VoiceAttention(), Now.AddSeconds(-3), "u01");
        attention.Subscriber("u00").Pinned =
            Enumerable.Range(10, 20).Select(i => $"u{i:D2}").ToList();

        var plan = Plan(room, attention, Now);

        Assert.That(AudiblePublishers(plan, "u00"),
            Has.Count.EqualTo(1 + Options.MaxPinnedPerSubscriber));
    }

    [Test]
    public void A_pin_on_somebody_who_has_left_is_ignored()
    {
        var room = Room(30);
        var attention = SpeakingNow(new VoiceAttention(), Now.AddSeconds(-3), "u01");
        attention.Subscriber("u00").Pinned = ["ghost"];

        var plan = Plan(room, attention, Now);

        Assert.That(AudiblePublishers(plan, "u00"), Is.EqualTo(new[] { "u01" }));
    }

    // ── Stability: the failure mode this design exists to prevent ─────────────

    /// <summary>
    /// A burst too short to count is heard, because entry is deliberately not gated - a listener
    /// who cannot hear somebody talking is a broken call.
    /// </summary>
    [Test]
    public void A_burst_too_short_to_count_earns_no_hold()
    {
        var room = Room(30);
        var attention = SpeakingNow(new VoiceAttention(), Now.AddSeconds(-3), "u01");
        Plan(room, attention, Now);

        SpeakingNow(attention, Now, "u17");
        var during = Plan(room, attention, Now);

        // The burst ends without ever qualifying, so nothing credits LastSpokeAt.
        attention.Speaker("u17").IsSpeaking = false;
        attention.Speaker("u17").SpeakingSinceUnixMs = 0;
        var afterDwell = Plan(room, attention, Now + Options.MinimumDwell + TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(during.ActiveSpeakers, Does.Contain("u17"));
            Assert.That(afterDwell.ActiveSpeakers, Is.EqualTo(new[] { "u01" }),
                "a burst that earned no hold keeps its slot for the dwell floor and not one second "
                + "longer");
        });
    }

    [Test]
    public void A_speaker_who_pauses_between_sentences_keeps_their_slot()
    {
        var room = Room(30);
        var attention = SpeakingNow(new VoiceAttention(), Now.AddSeconds(-5), "u01");
        Plan(room, attention, Now);

        SpokeAt(attention, Now, "u01");
        var later = Now + Options.SpeakerHoldTime - TimeSpan.FromSeconds(1);
        var plan = Plan(room, attention, later);

        Assert.That(plan.ActiveSpeakers, Is.EqualTo(new[] { "u01" }),
            "the gap between two sentences is not a departure, and demoting on it would flap the "
            + "set at conversational frequency");
    }

    [Test]
    public void An_incumbent_under_the_dwell_floor_is_not_displaced()
    {
        var options = Options with { ActiveSpeakerCount = 2, MaxActiveSpeakers = 2 };
        var room = Room(30);

        var attention = SpeakingNow(new VoiceAttention(), Now.AddSeconds(-3), "u01", "u02");
        Plan(room, attention, Now, options);

        // One second later - well inside the dwell window - u01 has gone quiet long enough to have
        // lost its hold, and u03 has a better claim on the slot than they do.
        var later = Now.AddSeconds(1);
        SpokeAt(attention, Now - Options.SpeakerHoldTime - TimeSpan.FromMinutes(1), "u01");
        SpokeAt(attention, later.AddSeconds(-1), "u03");

        var plan = Plan(room, attention, later, options);

        Assert.Multiple(() =>
        {
            Assert.That(plan.ActiveSpeakers, Does.Contain("u01"),
                "dwell is the floor under a slot's lifetime whatever the ranking says, and without "
                + "it the set would swap on every syllable");
            Assert.That(plan.ActiveSpeakers, Does.Not.Contain("u03"));
        });
    }

    [Test]
    public void An_incumbent_past_both_hold_and_dwell_gives_the_slot_up()
    {
        var options = Options with { ActiveSpeakerCount = 2, MaxActiveSpeakers = 2 };
        var room = Room(30);

        var attention = SpeakingNow(new VoiceAttention(), Now.AddSeconds(-3), "u01", "u02");
        Plan(room, attention, Now, options);

        var later = Now + Options.SpeakerHoldTime + Options.MinimumDwell + TimeSpan.FromSeconds(1);
        SpokeAt(attention, Now, "u01");
        SpokeAt(attention, later.AddSeconds(-1), "u03");

        var plan = Plan(room, attention, later, options);

        Assert.That(plan.ActiveSpeakers, Does.Contain("u03"),
            "the brakes bound how fast the set moves, they do not freeze it");
    }

    [Test]
    public void The_set_never_empties_once_it_has_been_established()
    {
        var room = Room(30);
        var attention = SpeakingNow(new VoiceAttention(), Now.AddSeconds(-3), "u01");
        Plan(room, attention, Now);

        SpokeAt(attention, Now, "u01");
        var muchLater = Now + Options.SpeakerHoldTime + Options.MinimumDwell + TimeSpan.FromMinutes(10);
        var plan = Plan(room, attention, muchLater);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Mode, Is.EqualTo(VoiceSubscriptionMode.ActiveSpeaker),
                "dropping back to all-to-all during a lull and climbing out again on the next word "
                + "is a mode flap that costs more than the quadratic it replaced");
            Assert.That(plan.ActiveSpeakers, Is.EqualTo(new[] { "u01" }));
        });
    }

    /// <summary>
    /// Twenty rounds of people starting and stopping inside the hold window, which is what a real
    /// conversation looks like to the server.
    /// </summary>
    [Test]
    public void The_set_settles_under_conversational_jitter()
    {
        var room = Room(30);
        var attention = new VoiceAttention();
        var clock = Now;

        SpeakingNow(attention, clock.AddSeconds(-3), "u01", "u02", "u03");
        var first = Plan(room, attention, clock);
        var revisions = new List<long> { first.Revision };

        for (var round = 0; round < 20; round++)
        {
            clock = clock.AddSeconds(1);

            // The same three keep taking turns: each stops, each starts again, nobody new arrives.
            var speaker = $"u{(round % 3) + 1:D2}";
            SpokeAt(attention, clock, speaker);
            SpeakingNow(attention, clock, speaker);

            revisions.Add(Plan(room, attention, clock).Revision);
        }

        Assert.Multiple(() =>
        {
            Assert.That(revisions.Distinct().ToList(), Has.Count.EqualTo(1),
                "twenty turns between the same three people is one subscription set, and every "
                + "extra revision here is an SDP exchange for every subscriber in the room");
            Assert.That(Plan(room, attention, clock).ActiveSpeakers.OrderBy(id => id, StringComparer.Ordinal),
                Is.EqualTo(new[] { "u01", "u02", "u03" }));
        });
    }

    // ── Capacity ──────────────────────────────────────────────────────────────

    [Test]
    public void Everyone_speaking_at_once_is_capped_rather_than_becoming_all_to_all_again()
    {
        var room = Room(30);
        var attention = new VoiceAttention();
        SpeakingNow(attention, Now.AddSeconds(-3),
            Enumerable.Range(0, 20).Select(i => $"u{i:D2}").ToArray());

        var plan = Plan(room, attention, Now);

        Assert.That(plan.ActiveSpeakers, Has.Count.EqualTo(Options.MaxActiveSpeakers));
    }

    [Test]
    public void A_live_speaker_is_admitted_even_past_the_nominal_slot_count()
    {
        var room = Room(30);
        var attention = new VoiceAttention();
        SpeakingNow(attention, Now.AddSeconds(-3),
            Enumerable.Range(0, Options.ActiveSpeakerCount + 2).Select(i => $"u{i:D2}").ToArray());

        var plan = Plan(room, attention, Now);

        Assert.That(plan.ActiveSpeakers, Has.Count.EqualTo(Options.ActiveSpeakerCount + 2),
            "a subscriber who cannot hear somebody who is talking is a broken call, and no saving "
            + "is worth that");
    }

    // ── Video: pause, screenshare audio, publisher cap, layers ────────────────

    private static VoiceParticipant Sharing(
        VoiceParticipant participant, string shareId, bool withAudio = false)
    {
        var share = new ActiveScreenShare
        {
            ShareId = shareId,
            TrackNames = [TrackNaming.ScreenTrack(shareId)],
            MediaSessionId = participant.MediaSessionId,
        };
        if (withAudio) share.TrackNames.Add(TrackNaming.ScreenAudioTrack(shareId));

        participant.IsStreaming = true;
        participant.ActiveScreenShares.Add(share);
        return participant;
    }

    [Test]
    public void Screenshare_audio_is_off_unless_the_subscriber_asked_for_it()
    {
        var room = Room(4);
        Sharing(room.Participants[0], "share-1", withAudio: true);

        var attention = new VoiceAttention();
        attention.Subscriber("u02").ScreenAudioShares = ["share-1"];

        var plan = Plan(room, attention, Now);

        Assert.Multiple(() =>
        {
            Assert.That(plan.For("u01").Tracks.Any(t => t.Kind == TrackNaming.ScreenAudioKind), Is.False,
                "most shares carry no meaningful audio and distributing it doubles the stream count "
                + "of the most expensive thing in the room");
            Assert.That(plan.For("u01").Tracks.Any(t => t.Kind == TrackNaming.ScreenKind), Is.True,
                "the video half is unaffected");
            Assert.That(plan.For("u02").Tracks.Any(t => t.Kind == TrackNaming.ScreenAudioKind), Is.True);
        });
    }

    [Test]
    public void A_paused_client_drops_video_and_keeps_audio()
    {
        var room = Room(4);
        Sharing(room.Participants[0], "share-1", withAudio: true);

        var attention = new VoiceAttention();
        attention.Subscriber("u01").IsPaused = true;
        attention.Subscriber("u01").ScreenAudioShares = ["share-1"];

        var set = Plan(room, attention, Now).For("u01");

        Assert.Multiple(() =>
        {
            Assert.That(set.Tracks.Any(t => t.Kind == TrackNaming.ScreenKind), Is.False,
                "a backgrounded tab should stop paying for pixels nobody is looking at");
            Assert.That(set.Tracks.Count(t => t.Kind == TrackNaming.AudioKind), Is.EqualTo(3),
                "silencing a backgrounded participant would turn a cost optimisation into a bug "
                + "report");
            Assert.That(set.Tracks.Any(t => t.Kind == TrackNaming.ScreenAudioKind), Is.True,
                "and a collapsed tile is not the same thing as a mute");
        });
    }

    [Test]
    public void A_collapsed_tile_drops_only_that_publisher()
    {
        var room = Room(4);
        Sharing(room.Participants[0], "share-a");
        Sharing(room.Participants[2], "share-b");

        var attention = new VoiceAttention();
        attention.Subscriber("u01").PausedPublishers = ["u00"];

        var set = Plan(room, attention, Now).For("u01");

        Assert.Multiple(() =>
        {
            Assert.That(set.Tracks.Any(t => t.ShareId == "share-a"), Is.False);
            Assert.That(set.Tracks.Any(t => t.ShareId == "share-b"), Is.True);
        });
    }

    [Test]
    public void Video_publishers_past_the_cap_are_not_distributed()
    {
        var options = Options with { MaxVideoPublishers = 2 };
        var room = Room(6);
        for (var i = 0; i < 4; i++) Sharing(room.Participants[i], $"share-{i}");

        var plan = Plan(room, new VoiceAttention(), Now, options);

        Assert.Multiple(() =>
        {
            Assert.That(plan.VideoPublishers, Is.EqualTo(new[] { "u00", "u01" }),
                "join order, so the cap answers the same way every time it is asked");
            Assert.That(plan.For("u05").Tracks.Where(t => t.ShareId is not null).Select(t => t.UserId),
                Is.EqualTo(new[] { "u00", "u01" }));
        });
    }

    /// <summary>
    /// A four-person call where somebody minimised a 1080p share is a room with a real plan and an
    /// all-to-all audio mode.
    /// </summary>
    [Test]
    public void A_small_room_where_somebody_paused_still_has_a_plan_worth_sending()
    {
        var room = Room(4);
        Sharing(room.Participants[0], "share-1");

        var quiet = Plan(room, new VoiceAttention(), Now);

        var attention = new VoiceAttention();
        attention.Subscriber("u01").IsPaused = true;
        var paused = Plan(room, attention, Now);

        Assert.Multiple(() =>
        {
            Assert.That(quiet.IsSelective, Is.False,
                "the ordinary small room sees no subscription set at all");
            Assert.That(paused.Mode, Is.EqualTo(VoiceSubscriptionMode.All));
            Assert.That(paused.IsSelective, Is.True);
        });
    }

    [Test]
    public void Screenshare_audio_being_off_by_default_counts_as_a_restriction()
    {
        var room = Room(4);
        Sharing(room.Participants[0], "share-1", withAudio: true);

        Assert.That(Plan(room, new VoiceAttention(), Now).IsSelective, Is.True);
    }

    [Test]
    public void A_camera_counts_against_the_video_publisher_cap()
    {
        var options = Options with { MaxVideoPublishers = 1 };
        var room = Room(5);
        room.Participants[0].ActiveVideoTracks.Add(
            new ActiveVideoTrack { TrackName = "camera", MediaSessionId = "cf-u00" });
        Sharing(room.Participants[1], "share-1");

        var plan = Plan(room, new VoiceAttention(), Now, options);

        Assert.Multiple(() =>
        {
            Assert.That(plan.VideoPublishers, Is.EqualTo(new[] { "u00" }),
                "camera is the more expensive half of the video bill, not a free one");
            Assert.That(plan.For("u02").Tracks.Any(t => t.ShareId == "share-1"), Is.False);
        });
    }

    [Test]
    public void A_paused_subscriber_drops_a_camera_as_well_as_a_share()
    {
        var room = Room(3);
        room.Participants[0].ActiveVideoTracks.Add(
            new ActiveVideoTrack { TrackName = "camera", MediaSessionId = "cf-u00" });

        var attention = new VoiceAttention();
        attention.Subscriber("u01").IsPaused = true;

        var plan = Plan(room, attention, Now);

        Assert.Multiple(() =>
        {
            Assert.That(plan.For("u01").Tracks.Any(t => t.TrackName == "camera"), Is.False);
            Assert.That(plan.For("u02").Tracks.Any(t => t.TrackName == "camera"), Is.True);
        });
    }

    [Test]
    public void A_camera_in_a_large_room_grid_is_pulled_at_a_lower_layer()
    {
        var room = Room(30);
        room.Participants[0].ActiveVideoTracks.Add(
            new ActiveVideoTrack { TrackName = "camera", MediaSessionId = "cf-u00" });
        var attention = SpeakingNow(new VoiceAttention(), Now.AddSeconds(-3), "u01");

        var plan = Plan(room, attention, Now);

        Assert.That(plan.For("u05").Tracks.Single(t => t.TrackName == "camera").Layer,
            Is.EqualTo(VoiceVideoLayers.Name(Options.GridLayer)),
            "nobody's camera tile in a thirty-person grid is full size, and this is the one place a "
            + "sensible guess beats the maximum");
    }

    [Test]
    public void The_simulcast_layer_follows_the_rendered_tile_size()
    {
        var room = Room(4);
        Sharing(room.Participants[0], "share-1");

        var attention = new VoiceAttention();
        attention.Subscriber("u01").TileHeights = new Dictionary<string, int> { ["u00"] = 120 };
        attention.Subscriber("u02").TileHeights = new Dictionary<string, int> { ["u00"] = 300 };

        var plan = Plan(room, attention, Now);

        Assert.Multiple(() =>
        {
            Assert.That(plan.For("u01").Tracks.Single(t => t.ShareId is not null).Layer,
                Is.EqualTo(VoiceVideoLayers.Low), "a viewer in a 200x120 tile does not need 1080p");
            Assert.That(plan.For("u02").Tracks.Single(t => t.ShareId is not null).Layer,
                Is.EqualTo(VoiceVideoLayers.Medium));
            Assert.That(plan.For("u03").Tracks.Single(t => t.ShareId is not null).Layer,
                Is.EqualTo(VoiceVideoLayers.High),
                "and a client that never reports a tile size sees exactly what it saw before");
        });
    }

    [Test]
    public void A_screenshare_is_never_downgraded_by_a_guess()
    {
        var room = Room(30);
        Sharing(room.Participants[0], "share-1");
        var attention = SpeakingNow(new VoiceAttention(), Now.AddSeconds(-3), "u01");

        var plan = Plan(room, attention, Now);

        Assert.That(plan.For("u05").Tracks.Single(t => t.ShareId is not null).Layer,
            Is.EqualTo(VoiceVideoLayers.High),
            "a 360p share is unreadable, and an unreadable share is a broken feature rather than a "
            + "cheap one - only a measured tile size may lower it");
    }

    [Test]
    public void A_share_carries_the_session_it_was_published_on()
    {
        var room = Room(3);
        room.Participants[0].ActiveScreenShares.Add(new ActiveScreenShare
        {
            ShareId = "share-1",
            TrackNames = [TrackNaming.ScreenTrack("share-1")],
            MediaSessionId = "cf-secondary",
        });

        var plan = Plan(room, new VoiceAttention(), Now);

        Assert.That(plan.For("u01").Tracks.Single(t => t.ShareId == "share-1").MediaSessionId,
            Is.EqualTo("cf-secondary"),
            "a desktop client publishes screen on a session of its own, and pulling it from the "
            + "publisher's microphone session names a track that session does not have");
    }

    // ── Housekeeping ──────────────────────────────────────────────────────────

    [Test]
    public void A_departed_participant_loses_their_slot()
    {
        var room = Room(30);
        var attention = SpeakingNow(new VoiceAttention(), Now.AddSeconds(-3), "u01", "u02");
        Plan(room, attention, Now);

        room.Participants.RemoveAll(p => p.UserId == "u01");
        attention.PruneTo(room.AllUserIds());
        var plan = Plan(room, attention, Now);

        Assert.Multiple(() =>
        {
            Assert.That(plan.ActiveSpeakers, Is.EqualTo(new[] { "u02" }));
            Assert.That(attention.Speakers.ContainsKey("u01"), Is.False,
                "a long-lived channel must not accumulate the attention state of everybody who was "
                + "ever in it");
        });
    }

    [Test]
    public void Planning_disabled_leaves_every_room_all_to_all()
    {
        var options = Options with { Enabled = false };
        var room = Room(50);
        var attention = SpeakingNow(new VoiceAttention(), Now.AddSeconds(-3), "u01");

        var plan = Plan(room, attention, Now, options);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Mode, Is.EqualTo(VoiceSubscriptionMode.All));
            Assert.That(plan.TotalSubscriptions, Is.EqualTo(50L * 49));
        });
    }
}
