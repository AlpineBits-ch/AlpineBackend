using System.Text.Json;

namespace Bots.Tests.Helpers;

/// <summary>Parses the JSON envelope GatewayConnectionRegistry.PublishAsync writes to Redis
/// (private DispatchEnvelope record: BotUserId/EventName/Data) so tests can assert on what a
/// handler fanned out without needing access to that private type.</summary>
internal static class DispatchAssertions
{
    public static (string BotUserId, string EventName, JsonElement Data) Parse(FakeSubscriber.Published message)
    {
        var json = JsonDocument.Parse(message.Message!.ToString()).RootElement;
        return (
            json.GetProperty("BotUserId").GetString()!,
            json.GetProperty("EventName").GetString()!,
            json.GetProperty("Data"));
    }
}
