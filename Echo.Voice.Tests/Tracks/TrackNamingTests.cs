using Echo.Voice.Tracks;

namespace Echo.Voice.Tests.Tracks;

/// <summary>
/// Pins the track-name convention that five inline copies previously reimplemented.
/// </summary>
[TestFixture]
public class TrackNamingTests
{
    // ── Normal ────────────────────────────────────────────────────────────────

    [Test]
    public void Microphone_track_is_audio_and_belongs_to_no_share()
    {
        var track = TrackNaming.Describe("audio");

        Assert.Multiple(() =>
        {
            Assert.That(track.Kind, Is.EqualTo("audio"));
            Assert.That(track.ShareId, Is.Null);
            Assert.That(track.TrackName, Is.EqualTo("audio"));
        });
    }

    [Test]
    public void Screen_track_yields_the_share_id()
    {
        var track = TrackNaming.Describe("screen-abc123");

        Assert.Multiple(() =>
        {
            Assert.That(track.Kind, Is.EqualTo("screen"));
            Assert.That(track.ShareId, Is.EqualTo("abc123"));
        });
    }

    [Test]
    public void Camera_track_is_video_and_belongs_to_no_share()
    {
        var track = TrackNaming.Describe("camera");

        Assert.Multiple(() =>
        {
            Assert.That(track.Kind, Is.EqualTo("video"));
            Assert.That(track.ShareId, Is.Null);
        });
    }

    [Test]
    public void Builders_round_trip_through_Describe()
    {
        var screen = TrackNaming.Describe(TrackNaming.ScreenTrack("s1"));
        var screenAudio = TrackNaming.Describe(TrackNaming.ScreenAudioTrack("s1"));

        Assert.Multiple(() =>
        {
            Assert.That(screen.Kind, Is.EqualTo("screen"));
            Assert.That(screen.ShareId, Is.EqualTo("s1"));
            Assert.That(screenAudio.Kind, Is.EqualTo("screenAudio"));
            Assert.That(screenAudio.ShareId, Is.EqualTo("s1"));
        });
    }

    // ── Edge ──────────────────────────────────────────────────────────────────

    /// <summary>The trap every inline copy carried.</summary>
    [Test]
    public void Screen_audio_is_not_mistaken_for_a_screen_track()
    {
        var track = TrackNaming.Describe("screen-audio-xyz");

        Assert.Multiple(() =>
        {
            Assert.That(track.Kind, Is.EqualTo("screenAudio"));
            Assert.That(track.ShareId, Is.EqualTo("xyz"), "the share id must not keep the 'audio-' infix");
        });
    }

    /// <summary>A share whose id itself begins with "audio-" is the adversarial case for the
    /// ordering above: both prefixes match and the longer one still has to win.</summary>
    [Test]
    public void Share_id_beginning_with_audio_still_classifies_by_the_longest_prefix()
    {
        var screen = TrackNaming.Describe("screen-audio-1");

        Assert.Multiple(() =>
        {
            Assert.That(screen.Kind, Is.EqualTo("screenAudio"));
            Assert.That(screen.ShareId, Is.EqualTo("1"));
        });
    }

    [Test]
    public void Bare_prefix_yields_an_empty_share_id_rather_than_throwing()
    {
        var track = TrackNaming.Describe("screen-");

        Assert.Multiple(() =>
        {
            Assert.That(track.Kind, Is.EqualTo("screen"));
            Assert.That(track.ShareId, Is.Empty);
        });
    }

    [Test]
    public void Classification_is_case_sensitive()
    {
        // Ordinal comparison throughout - track names are opaque identifiers agreed with the
        // client, not user-facing text, so "Screen-x" is a camera track and not a screen share.
        var track = TrackNaming.Describe("Screen-x");

        Assert.That(track.Kind, Is.EqualTo("video"));
    }

    [Test]
    public void Empty_track_name_is_video_and_shareless()
    {
        var track = TrackNaming.Describe(string.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(track.Kind, Is.EqualTo("video"));
            Assert.That(track.ShareId, Is.Null);
        });
    }

    // ── Negative ──────────────────────────────────────────────────────────────

    [Test]
    public void A_name_merely_containing_screen_is_not_a_share()
    {
        var track = TrackNaming.Describe("my-screen-share");

        Assert.Multiple(() =>
        {
            Assert.That(track.Kind, Is.EqualTo("video"));
            Assert.That(track.ShareId, Is.Null);
        });
    }

    [Test]
    public void A_track_named_audio_with_a_suffix_is_not_the_microphone()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TrackNaming.IsMicrophone("audio2"), Is.False);
            Assert.That(TrackNaming.IsMicrophone("Audio"), Is.False);
            Assert.That(TrackNaming.IsMicrophone(null), Is.False);
            Assert.That(TrackNaming.IsMicrophone("audio"), Is.True);
        });
    }

    [Test]
    public void IsScreenShare_covers_both_halves_and_nothing_else()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TrackNaming.IsScreenShare("screen-a"), Is.True);
            Assert.That(TrackNaming.IsScreenShare("screen-audio-a"), Is.True);
            Assert.That(TrackNaming.IsScreenShare("audio"), Is.False);
            Assert.That(TrackNaming.IsScreenShare("camera"), Is.False);
        });
    }
}
