using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Echo.Realtime.Sfu;

/// <summary>A Cloudflare Calls HTTP call returned a non-success status.</summary>
public class CloudflareCallsException(string operation, System.Net.HttpStatusCode statusCode, string responseBody)
    : Exception($"Cloudflare Calls '{operation}' failed with {(int)statusCode} {statusCode}: {responseBody}")
{
    public string Operation { get; } = operation;
    public System.Net.HttpStatusCode StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody;
}

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

/// <summary>Thin relay over the Cloudflare Calls (SFU) HTTP API.</summary>
public class CloudflareService
{
    private readonly HttpClient _http;
    private readonly ILogger<CloudflareService> _logger;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public CloudflareService(IHttpClientFactory factory, ILogger<CloudflareService> logger)
    {
        _http = factory.CreateClient("CloudflareProxy");
        _logger = logger;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage res, string operation, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync(ct);
        _logger.LogError(
            "Cloudflare Calls {Operation} failed with {StatusCode}: {Body}",
            operation, (int)res.StatusCode, body);
        throw new CloudflareCallsException(operation, res.StatusCode, body);
    }

    public async Task<string> CreateSessionAsync(CancellationToken ct = default)
    {
        var res = await _http.PostAsync("sessions/new", null, ct);
        await EnsureSuccessAsync(res, "sessions/new", ct);
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
        await EnsureSuccessAsync(res, "tracks/new", ct);
        return (await res.Content.ReadFromJsonAsync<CfTracksNewResponse>(Json, ct))!;
    }

    public async Task<CfRenegotiateResponse> RenegotiateAsync(
        string cfSessionId,
        CfRenegotiateRequest request,
        CancellationToken ct = default)
    {
        var res = await _http.PutAsJsonAsync(
            $"sessions/{cfSessionId}/renegotiate", request, Json, ct);
        await EnsureSuccessAsync(res, "renegotiate", ct);
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
        if (res.StatusCode != System.Net.HttpStatusCode.NotAcceptable)
            await EnsureSuccessAsync(res, "tracks/close", ct);
    }
}
