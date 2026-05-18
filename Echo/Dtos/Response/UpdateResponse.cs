using System.Text.Json.Serialization;

namespace Echo.Dtos.Response;

public class UpdateResponse
{
    public string Version { get; set; }
    public string Notes { get; init; }
    [JsonPropertyName("pub_date")]
    public DateTime PublishedAt { get; init; }
    public string Url { get; init; }
    public string Signature { get; set; }
}