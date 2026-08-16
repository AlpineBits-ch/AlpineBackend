using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Echo.Realtime.LiveKit;

namespace Echo.Voice.Tests.LiveKit;

/// <summary>
/// The token is the entire authorization story for the SFU: it is verified offline, there is no
/// callback, and nothing this side does after minting one can narrow it.
/// </summary>
[TestFixture]
public class LiveKitTokenTests
{
    private const string ApiKey = "APItestkey";
    private const string ApiSecret = "a-secret-that-is-long-enough-to-be-a-key";

    private static readonly LiveKitOptions Options = new()
    {
        ApiKey = ApiKey,
        ApiSecret = ApiSecret,
        Nodes = [new LiveKitNode("fsn1", "wss://sfu-fsn1.venta.gg", "http://10.10.0.2:7880")],
    };

    private static JsonElement PayloadOf(string token)
    {
        var parts = token.Split('.');
        Assert.That(parts, Has.Length.EqualTo(3), "a JWT is three dot-separated segments");
        return JsonSerializer.Deserialize<JsonElement>(Decode(parts[1]));
    }

    private static byte[] Decode(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    // ── Signature ─────────────────────────────────────────────────────────────

    [Test]
    public void The_signature_is_HS256_over_the_header_and_payload()
    {
        var token = LiveKitToken.ForJoin(Options, "channel-1", "user-1");
        var parts = token.Split('.');

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(ApiSecret));
        var expected = Convert.ToBase64String(
                hmac.ComputeHash(Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}")))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.That(parts[2], Is.EqualTo(expected));
    }

    [Test]
    public void A_token_minted_with_a_different_secret_does_not_match()
    {
        // The property the whole model rests on: a client cannot widen its own grants, because any
        // edit invalidates the signature and the node verifies it locally.
        var mine = LiveKitToken.ForJoin(Options, "channel-1", "user-1");
        var theirs = LiveKitToken.ForJoin(
            Options with { ApiSecret = "a-different-secret-entirely-here" }, "channel-1", "user-1");

        Assert.That(mine.Split('.')[2], Is.Not.EqualTo(theirs.Split('.')[2]));
    }

    [Test]
    public void Minting_without_a_configured_key_throws_rather_than_signing_over_nothing()
    {
        Assert.Throws<InvalidOperationException>(
            () => LiveKitToken.ForJoin(new LiveKitOptions(), "channel-1", "user-1"),
            "an HMAC over an empty key is a valid signature that no node will ever accept, so it "
            + "would fail at the client with nothing on this side to explain it");
    }

    // ── Grants ────────────────────────────────────────────────────────────────

    /// <summary>The one that matters most.</summary>
    [Test]
    public void Every_permission_is_written_out_even_when_it_is_false()
    {
        var token = LiveKitToken.ForJoin(
            Options, "channel-1", "user-1", grants: LiveKitGrants.Listener);

        var video = PayloadOf(token).GetProperty("video");

        Assert.Multiple(() =>
        {
            Assert.That(video.GetProperty("canPublish").GetBoolean(), Is.False);
            Assert.That(video.GetProperty("canSubscribe").GetBoolean(), Is.True);
            Assert.That(video.GetProperty("canPublishData").GetBoolean(), Is.False);
            Assert.That(video.GetProperty("roomJoin").GetBoolean(), Is.True);
            Assert.That(video.GetProperty("room").GetString(), Is.EqualTo("channel-1"));
        });
    }

    /// <summary>What makes an audio-only participant real rather than advisory.</summary>
    [Test]
    public void An_audio_only_participant_is_granted_the_microphone_source_and_no_other()
    {
        var token = LiveKitToken.ForJoin(
            Options, "channel-1", "user-1", grants: LiveKitGrants.AudioOnly);

        var sources = PayloadOf(token).GetProperty("video").GetProperty("canPublishSources")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(sources, Is.EqualTo(new[] { LiveKitSources.Microphone }));
            Assert.That(sources, Does.Not.Contain(LiveKitSources.Camera));
            Assert.That(sources, Does.Not.Contain(LiveKitSources.ScreenShare));
        });
    }

    /// <summary>An empty source list is a different claim from an absent one - "may publish nothing"
    /// rather than "canPublish decides" - so the unconstrained case must leave the field off.</summary>
    [Test]
    public void A_full_participant_carries_no_source_list_at_all()
    {
        var token = LiveKitToken.ForJoin(
            Options, "channel-1", "user-1", grants: LiveKitGrants.Participant);

        Assert.That(
            PayloadOf(token).GetProperty("video").TryGetProperty("canPublishSources", out _),
            Is.False);
    }

    [Test]
    public void An_admin_token_carries_the_control_plane_grants_and_no_join()
    {
        var video = PayloadOf(LiveKitToken.ForAdmin(Options, "channel-1")).GetProperty("video");

        Assert.Multiple(() =>
        {
            Assert.That(video.GetProperty("roomCreate").GetBoolean(), Is.True);
            Assert.That(video.GetProperty("roomAdmin").GetBoolean(), Is.True);
            Assert.That(video.GetProperty("roomList").GetBoolean(), Is.True);
            Assert.That(video.GetProperty("room").GetString(), Is.EqualTo("channel-1"));
            Assert.That(video.TryGetProperty("roomJoin", out _), Is.False,
                "an admin token is for the control plane, not for sitting in a room");
        });
    }

    // ── Identity and lifetime ─────────────────────────────────────────────────

    [Test]
    public void The_identity_appears_as_both_sub_and_identity()
    {
        var payload = PayloadOf(LiveKitToken.ForJoin(Options, "channel-1", "user-1"));

        Assert.Multiple(() =>
        {
            Assert.That(payload.GetProperty("sub").GetString(), Is.EqualTo("user-1"));
            Assert.That(payload.GetProperty("identity").GetString(), Is.EqualTo("user-1"));
            Assert.That(payload.GetProperty("iss").GetString(), Is.EqualTo(ApiKey));
        });
    }

    /// <summary>A null display name must be absent rather than serialised as <c>"name": null</c>,
    /// which is what <c>JsonIgnoreCondition.WhenWritingNull</c> would leave it as - the option
    /// applies to object properties, not to dictionary values - and would render to the room as an
    /// empty label.</summary>
    [Test]
    public void An_absent_display_name_is_omitted_rather_than_written_as_null()
    {
        Assert.That(
            PayloadOf(LiveKitToken.ForJoin(Options, "channel-1", "user-1")).TryGetProperty("name", out _),
            Is.False);
    }

    [Test]
    public void A_display_name_is_carried_when_there_is_one()
    {
        Assert.That(
            PayloadOf(LiveKitToken.ForJoin(Options, "channel-1", "user-1", "Dominic"))
                .GetProperty("name").GetString(),
            Is.EqualTo("Dominic"));
    }

    /// <summary>
    /// A join token is used once, within seconds, to open a WebSocket, and is never consulted
    /// again.
    /// </summary>
    [Test]
    public void A_join_token_expires_in_minutes_not_hours()
    {
        var payload = PayloadOf(LiveKitToken.ForJoin(Options, "channel-1", "user-1"));
        var lifetime = payload.GetProperty("exp").GetInt64() - payload.GetProperty("nbf").GetInt64();

        Assert.That(lifetime, Is.EqualTo((long)Options.JoinTokenTtl.TotalSeconds));
        Assert.That(lifetime, Is.LessThanOrEqualTo(600),
            "the reference implementation defaults to six hours, which is a capability sitting in a "
            + "log for the rest of the afternoon");
    }

    [Test]
    public void An_admin_token_is_shorter_still()
    {
        var payload = PayloadOf(LiveKitToken.ForAdmin(Options));
        var lifetime = payload.GetProperty("exp").GetInt64() - payload.GetProperty("nbf").GetInt64();

        Assert.That(lifetime, Is.LessThan((long)Options.JoinTokenTtl.TotalSeconds));
    }
}

/// <summary>The identity scheme, which decides who evicts whom.</summary>
[TestFixture]
public class LiveKitIdentityTests
{
    [Test]
    public void A_primary_identity_is_the_bare_user_id()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LiveKitIdentity.Primary("user-1"), Is.EqualTo("user-1"));
            Assert.That(LiveKitIdentity.IsPrimary("user-1"), Is.True);
        });
    }

    [Test]
    public void A_secondary_identity_is_tagged_so_it_cannot_evict_the_primary_one()
    {
        var secondary = LiveKitIdentity.Secondary("user-1", "screen");

        Assert.Multiple(() =>
        {
            Assert.That(secondary, Is.Not.EqualTo(LiveKitIdentity.Primary("user-1")),
                "an identical identity would kick the user's own microphone off the call");
            Assert.That(LiveKitIdentity.IsPrimary(secondary), Is.False);
            Assert.That(LiveKitIdentity.UserOf(secondary), Is.EqualTo("user-1"));
        });
    }

    /// <summary>A tag carrying a separator would re-point <see cref="LiveKitIdentity.UserOf"/> at
    /// somebody else, which is how a participant ends up attributed to the wrong account in every
    /// roster repair that reads an identity back.</summary>
    [Test]
    public void A_tag_cannot_smuggle_a_separator_in()
    {
        var identity = LiveKitIdentity.Secondary("user-1", "screen#user-2");

        Assert.That(LiveKitIdentity.UserOf(identity), Is.EqualTo("user-1"));
    }

    [Test]
    public void A_tag_of_nothing_usable_still_produces_a_distinct_identity()
    {
        var identity = LiveKitIdentity.Secondary("user-1", "###");

        Assert.Multiple(() =>
        {
            Assert.That(LiveKitIdentity.IsPrimary(identity), Is.False);
            Assert.That(LiveKitIdentity.UserOf(identity), Is.EqualTo("user-1"));
        });
    }
}
