using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bots.Contracts.Gateway.Payloads;

public class HelloPayload
{
    [JsonPropertyName("heartbeat_interval")]
    public int HeartbeatInterval { get; set; }
}

/// <summary>Inbound OP 2 Identify payload a connecting bot sends.</summary>
public class IdentifyPayload
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("intents")]
    public long Intents { get; set; }

    [JsonPropertyName("properties")]
    public JsonElement? Properties { get; set; }
}

/// <summary>Inbound OP 6 Resume payload. Its fields are read but never acted on - resume isn't
/// supported (see Bots.Application/Gateway design notes); every attempt gets OP 9 Invalid Session.</summary>
public class ResumePayload
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("seq")]
    public int Seq { get; set; }
}
