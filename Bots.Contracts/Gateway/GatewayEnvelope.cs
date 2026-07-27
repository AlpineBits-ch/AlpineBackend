using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bots.Contracts.Gateway;

/// <summary>
/// Discord's Gateway wire envelope, {op, d, s, t}. Used for inbound frames - `d`'s shape depends
/// on `op`/`t`, so it's kept as a raw JsonElement here and deserialized further once the caller
/// knows which opcode it's looking at.
/// </summary>
public class GatewayEnvelope
{
    [JsonPropertyName("op")]
    public int Op { get; set; }

    [JsonPropertyName("d")]
    public JsonElement? D { get; set; }

    [JsonPropertyName("s")]
    public int? S { get; set; }

    [JsonPropertyName("t")]
    public string? T { get; set; }
}

/// <summary>Outbound envelope with a strongly-typed payload, for the frames the server sends.</summary>
public class GatewayOutboundEnvelope<TPayload>
{
    [JsonPropertyName("op")]
    public int Op { get; set; }

    [JsonPropertyName("d")]
    public TPayload? D { get; set; }

    [JsonPropertyName("s")]
    public int? S { get; set; }

    [JsonPropertyName("t")]
    public string? T { get; set; }
}
