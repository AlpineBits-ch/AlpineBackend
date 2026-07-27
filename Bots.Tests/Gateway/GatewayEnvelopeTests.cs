using System.Text.Json;
using Bots.Contracts.Gateway;
using Bots.Contracts.Gateway.Payloads;

namespace Bots.Tests.Gateway;

/// <summary>
/// Covers the {op, d, s, t} wire envelope real Discord Gateway clients deserialize - a shape
/// mismatch here (wrong casing, missing field) is exactly the kind of bug that only shows up
/// against a real client library, so these pin the exact JSON property names.
/// </summary>
[TestFixture]
public class GatewayEnvelopeTests
{
    [Test]
    public void OutboundEnvelope_SerializesWithDiscordsExactFieldNames()
    {
        var envelope = new GatewayOutboundEnvelope<HelloPayload>
        {
            Op = (int)GatewayOpCode.Hello,
            D = new HelloPayload { HeartbeatInterval = 41250 },
        };

        var json = JsonSerializer.Serialize(envelope);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("op").GetInt32(), Is.EqualTo(10));
            Assert.That(root.GetProperty("d").GetProperty("heartbeat_interval").GetInt32(), Is.EqualTo(41250));
        });
    }

    [Test]
    public void InboundEnvelope_DeserializesIdentifyPayload()
    {
        var json = """
        {
            "op": 2,
            "d": {
                "token": "abc123",
                "intents": 513,
                "properties": { "os": "linux" }
            }
        }
        """;

        var envelope = JsonSerializer.Deserialize<GatewayEnvelope>(json)!;
        var identify = envelope.D!.Value.Deserialize<IdentifyPayload>()!;

        Assert.Multiple(() =>
        {
            Assert.That(envelope.Op, Is.EqualTo((int)GatewayOpCode.Identify));
            Assert.That(identify.Token, Is.EqualTo("abc123"));
            Assert.That(identify.Intents, Is.EqualTo(513));
        });
    }

    [Test]
    public void DispatchEnvelope_IncludesSequenceAndEventNameFields()
    {
        var envelope = new GatewayOutboundEnvelope<MessageCreatePayload>
        {
            Op = (int)GatewayOpCode.Dispatch,
            T = "MESSAGE_CREATE",
            S = 7,
            D = new MessageCreatePayload { Id = "msg_1", ChannelId = "chan_1", Content = "hi" },
        };

        var json = JsonSerializer.Serialize(envelope);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("t").GetString(), Is.EqualTo("MESSAGE_CREATE"));
            Assert.That(root.GetProperty("s").GetInt32(), Is.EqualTo(7));
            Assert.That(root.GetProperty("d").GetProperty("content").GetString(), Is.EqualTo("hi"));
        });
    }

    [Test]
    public void DiscordUserPayload_DefaultsToBotDiscriminatorZero()
    {
        var user = new DiscordUserPayload { Id = "user_1", Username = "TestBot" };

        Assert.Multiple(() =>
        {
            Assert.That(user.Discriminator, Is.EqualTo("0000"));
            Assert.That(user.Bot, Is.True);
        });
    }
}
