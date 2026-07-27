using System.Text.Json.Serialization;

namespace Bots.Contracts.Gateway.Payloads;

public class ReadyPayload
{
    [JsonPropertyName("v")]
    public int V { get; set; } = 10;

    [JsonPropertyName("user")]
    public DiscordUserPayload User { get; set; } = null!;

    /// <summary>Discord itself sends unavailable guild stubs in READY and lazy-loads the real
    /// data via a GUILD_CREATE burst right after - we follow the same shape, one GUILD_CREATE
    /// per guild the bot is installed in.</summary>
    [JsonPropertyName("guilds")]
    public List<UnavailableGuildPayload> Guilds { get; set; } = new();

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("resume_gateway_url")]
    public string ResumeGatewayUrl { get; set; } = "";

    [JsonPropertyName("application")]
    public ApplicationPayload Application { get; set; } = null!;
}

public class UnavailableGuildPayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("unavailable")]
    public bool Unavailable { get; set; } = true;
}

public class ApplicationPayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("flags")]
    public int Flags { get; set; }
}
