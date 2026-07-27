using System.Text.Json;
using Bots.Contracts.Gateway;
using Bots.Contracts.Gateway.Payloads;

namespace Bots.Tests.Gateway;

/// <summary>Pins the INTERACTION_CREATE dispatch shape and the bot's inbound callback/followup
/// shapes against Discord's exact field names - the same kind of shape mismatch that would only
/// otherwise surface against a real client library, per the MESSAGE_CREATE work.</summary>
[TestFixture]
public class InteractionPayloadTests
{
    [Test]
    public void InteractionCreateDispatch_SerializesWithDiscordsExactFieldNames()
    {
        var envelope = new GatewayOutboundEnvelope<InteractionPayload>
        {
            Op = (int)GatewayOpCode.Dispatch,
            T = "INTERACTION_CREATE",
            S = 1,
            D = new InteractionPayload
            {
                Id = "intr_1",
                ApplicationId = "user_bot1",
                Type = 2,
                Data = new InteractionDataPayload
                {
                    Id = "boco_1",
                    Name = "ping",
                    Type = 1,
                    Options = [new InteractionOptionPayload { Name = "target", Type = 3, Value = JsonSerializer.SerializeToElement("world") }],
                },
                GuildId = "gild_1",
                ChannelId = "chan_1",
                Token = "secret-token",
                Version = 1,
            },
        };

        var json = JsonSerializer.Serialize(envelope);
        using var doc = JsonDocument.Parse(json);
        var d = doc.RootElement.GetProperty("d");

        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("t").GetString(), Is.EqualTo("INTERACTION_CREATE"));
            Assert.That(d.GetProperty("application_id").GetString(), Is.EqualTo("user_bot1"));
            Assert.That(d.GetProperty("guild_id").GetString(), Is.EqualTo("gild_1"));
            Assert.That(d.GetProperty("channel_id").GetString(), Is.EqualTo("chan_1"));
            Assert.That(d.GetProperty("data").GetProperty("name").GetString(), Is.EqualTo("ping"));
            Assert.That(d.GetProperty("data").GetProperty("options")[0].GetProperty("value").GetString(), Is.EqualTo("world"));
        });
    }

    [Test]
    public void InteractionCallback_DeserializesImmediateResponse()
    {
        var json = """
        {
            "type": 4,
            "data": { "content": "pong", "flags": 0 }
        }
        """;

        var callback = JsonSerializer.Deserialize<InteractionCallbackPayload>(json)!;

        Assert.Multiple(() =>
        {
            Assert.That(callback.Type, Is.EqualTo(4));
            Assert.That(callback.Data!.Content, Is.EqualTo("pong"));
        });
    }

    [Test]
    public void InteractionCallback_DeferredResponse_HasNoContentYet()
    {
        var json = """{ "type": 5 }""";

        var callback = JsonSerializer.Deserialize<InteractionCallbackPayload>(json)!;

        Assert.Multiple(() =>
        {
            Assert.That(callback.Type, Is.EqualTo(5));
            Assert.That(callback.Data, Is.Null);
        });
    }
}
