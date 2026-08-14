using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Echo.Realtime.Sfu;

/// <summary>A Cloudflare Calls HTTP call returned a non-success status.</summary>
public class CloudflareCallsException(
    string operation,
    System.Net.HttpStatusCode statusCode,
    string responseBody,
    IReadOnlyList<string>? trackErrorCodes = null,
    string? errorCode = null)
    : Exception($"Cloudflare Calls '{operation}' failed with {(int)statusCode} {statusCode}: {responseBody}")
{
    public string Operation { get; } = operation;
    public System.Net.HttpStatusCode StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody;

    /// <summary>
    /// The top-level <c>errorCode</c> Cloudflare reported, when it rejected the request as a whole
    /// rather than per track.
    /// </summary>
    public string? ErrorCode { get; } = errorCode;

    /// <summary>
    /// The per-track <c>errorCode</c>s Cloudflare reported, when the failure was a 200 carrying
    /// track errors rather than a rejected request.
    /// </summary>
    public IReadOnlyList<string> TrackErrorCodes { get; } = trackErrorCodes ?? [];
}

public record CfSessionDescription(string Type, string Sdp);

/// <summary>Which simulcast layer a pulled track should be served at.</summary>
public record CfSimulcast(
    string PreferredRid,
    string RidNotAvailable = CfSimulcast.NextAvailable,
    string PriorityOrdering = CfSimulcast.NoOrdering
)
{
    public const string NoOrdering = "none";

    /// <summary>Serve the next rid the publisher actually has, rather than nothing.</summary>
    public const string NextAvailable = "desc";
}

public record CfTrackNew(
    string Location,          // "local" | "remote"
    string? Mid = null,       // local tracks: transceiver MID after setLocalDescription
    string? TrackName = null, // local: name to publish; remote: name to subscribe to
    string? SessionId = null, // remote tracks: the peer's CF session ID
    CfSimulcast? Simulcast = null // remote tracks: which layer to serve
);

public record CfTracksNewRequest(
    CfSessionDescription SessionDescription,
    List<CfTrackNew> Tracks
);

/// <summary>One entry of Cloudflare's <c>tracks/new</c> response.</summary>
public record CfTrackResult(
    string? Mid,
    string TrackName,
    string? SessionId,
    string? Location,
    string? ErrorCode,
    string? ErrorDescription
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

    /// <summary>Cloudflare's code for "this session's PeerConnection is not connected".</summary>
    public const string SessionErrorCode = "session_error";

    /// <summary>Cloudflare's top-level <c>errorCode</c>, when the body carries one.</summary>
    private static string? TopLevelErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("errorCode", out var code)
                   && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage res, string operation, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync(ct);
        _logger.LogError(
            "Cloudflare Calls {Operation} failed with {StatusCode}: {Body}",
            operation, (int)res.StatusCode, body);
        throw new CloudflareCallsException(
            operation, res.StatusCode, body, errorCode: TopLevelErrorCode(body));
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
        var body = await res.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<CfTracksNewResponse>(body, Json);

        // Track failures are checked first, and the order is load-bearing.
        EnsureNoTrackFailures(result, "tracks/new", body);
        EnsureValidSessionDescription(result?.SessionDescription, "tracks/new", body);
        return result!;
    }

    /// <summary>Backoff between attempts to pull a remote track.</summary>
    public static readonly IReadOnlyList<TimeSpan> SubscribeRetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromMilliseconds(1000),
        TimeSpan.FromMilliseconds(1500),
        TimeSpan.FromMilliseconds(2000),
    ];

    /// <summary>
    /// <see cref="TracksNewAsync"/> for a pull, retried across the window in which the publisher
    /// exists but is not yet sending.
    /// </summary>
    public async Task<CfTracksNewResponse> SubscribeTracksAsync(
        string cfSessionId,
        CfTracksNewRequest request,
        CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await TracksNewAsync(cfSessionId, request, ct);
            }
            catch (CloudflareCallsException ex)
                when (attempt < SubscribeRetryDelays.Count && IsTransient(ex))
            {
                _logger.LogWarning(
                    "Subscribe tracks/new attempt {Attempt} of {Total} failed for session "
                    + "{CfSessionId}, retrying in {DelayMs}ms: {Message}",
                    attempt + 1, SubscribeRetryDelays.Count + 1, cfSessionId,
                    SubscribeRetryDelays[attempt].TotalMilliseconds, ex.Message);
                await Task.Delay(SubscribeRetryDelays[attempt], ct);
            }
        }
    }

    /// <summary>Whether a failed pull is worth trying again.</summary>
    private static bool IsTransient(CloudflareCallsException ex) =>
        !string.Equals(ex.ErrorCode, SessionErrorCode, StringComparison.OrdinalIgnoreCase)
        && (ex.StatusCode == System.Net.HttpStatusCode.OK || (int)ex.StatusCode >= 500);

    public async Task<CfRenegotiateResponse> RenegotiateAsync(
        string cfSessionId,
        CfRenegotiateRequest request,
        CancellationToken ct = default)
    {
        var res = await _http.PutAsJsonAsync(
            $"sessions/{cfSessionId}/renegotiate", request, Json, ct);
        await EnsureSuccessAsync(res, "renegotiate", ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<CfRenegotiateResponse>(body, Json);
        EnsureValidSessionDescription(result?.SessionDescription, "renegotiate", body);
        return result!;
    }

    /// <summary>
    /// EnsureSuccessAsync above only catches Cloudflare rejecting the request outright (non-2xx).
    /// </summary>
    private void EnsureValidSessionDescription(CfSessionDescription? desc, string operation, string rawBody)
    {
        if (desc is not null && !string.IsNullOrEmpty(desc.Sdp) && !string.IsNullOrEmpty(desc.Type)) return;

        _logger.LogError(
            "Cloudflare Calls {Operation} returned a 2xx response with a missing or empty " +
            "sessionDescription. Raw body: {Body}", operation, rawBody);
        throw new CloudflareCallsException(operation, System.Net.HttpStatusCode.OK, rawBody);
    }

    /// <summary>Turns a per-track failure into a real failure.</summary>
    private void EnsureNoTrackFailures(CfTracksNewResponse? result, string operation, string rawBody)
    {
        if (result?.Tracks is null) return;

        var failed = result.Tracks
            .Where(t => !string.IsNullOrEmpty(t.ErrorCode)
                        || !string.IsNullOrEmpty(t.ErrorDescription)
                        || string.IsNullOrEmpty(t.Mid))
            .ToList();
        if (failed.Count == 0) return;

        _logger.LogError(
            "Cloudflare Calls {Operation} returned 200 but {Count} track(s) failed: {Failures}. Raw body: {Body}",
            operation, failed.Count,
            string.Join(", ", failed.Select(t =>
                $"{t.TrackName}({t.ErrorCode ?? "no-error-code"}: {t.ErrorDescription ?? "no mid returned"})")),
            rawBody);

        throw new CloudflareCallsException(
            operation, System.Net.HttpStatusCode.OK, rawBody,
            failed.Select(t => t.ErrorCode).Where(c => !string.IsNullOrEmpty(c)).ToList()!);
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
