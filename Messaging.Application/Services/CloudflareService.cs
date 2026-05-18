using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
public record CfSessionDescription(string Type, string Sdp);

public record CfTrackNew(
    string Location,          // "local" | "remote"
    string? Mid = null,       // local tracks: transceiver MID after setLocalDescription
    string? TrackName = null, // local: name to publish; remote: name to subscribe to
    string? SessionId = null  // remote tracks: the peer's CF session ID
);

public record CfTracksNewRequest(
    CfSessionDescription SessionDescription,
    List<CfTrackNew> Tracks
);

public record CfTrackResult(
    string Mid,
    string TrackName,
    string? SessionId,
    string? Location,
    string? Error
);

public record CfTracksNewResponse(
    CfSessionDescription SessionDescription,
    List<CfTrackResult> Tracks,
    bool RequiresImmediateRenegotiation
);

public record CfRenegotiateRequest(CfSessionDescription SessionDescription);
public record CfRenegotiateResponse(CfSessionDescription SessionDescription);


public class CloudflareService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public CloudflareService(IHttpClientFactory factory)
    {
        _http = factory.CreateClient("CloudflareProxy");
    }

    public async Task<string> CreateSessionAsync(CancellationToken ct = default)
    {
        // Since BaseAddress is set in Program.cs, we start the string with "sessions/new"
        var res = await _http.PostAsync("sessions/new", null, ct);
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return doc.GetProperty("sessionId").GetString()!;
    }

    public async Task<CfTracksNewResponse> TracksNewAsync(
        string cfSessionId,
        CfTracksNewRequest request,
        CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync(
            $"sessions/{cfSessionId}/tracks/new", request, Json, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<CfTracksNewResponse>(Json, ct))!;
    }

    public async Task<CfRenegotiateResponse> RenegotiateAsync(
        string cfSessionId,
        CfRenegotiateRequest request,
        CancellationToken ct = default)
    {
        var res = await _http.PutAsJsonAsync(
            $"sessions/{cfSessionId}/renegotiate", request, Json, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<CfRenegotiateResponse>(Json, ct))!;
    }

    public async Task CloseTracksAsync(
        string cfSessionId,
        IEnumerable<string> trackNames,
        CancellationToken ct = default)
    {
        var body = new { tracks = trackNames.Select(t => new { trackName = t }), force = true };
        var res = await _http.PutAsJsonAsync(
            $"sessions/{cfSessionId}/tracks/close", body, Json, ct);
        res.EnsureSuccessStatusCode();
    }
}