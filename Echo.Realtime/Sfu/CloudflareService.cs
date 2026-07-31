using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Echo.Realtime.Sfu;

/// <summary>
/// A Cloudflare Calls HTTP call returned a non-success status. Carries the
/// raw response body — Cloudflare's `EnsureSuccessStatusCode()` alone
/// discards it, turning every failure into an opaque 500 with no way to
/// tell a bad request apart from a transient SFU-side race. See the
/// venta_mobile "no audio in/out" investigation: a subscribe-side
/// `tracks/new` failure here was completely silent to the end user because
/// nothing upstream could see why Cloudflare rejected it.
/// </summary>
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

/// <summary>
/// One entry of Cloudflare's <c>tracks/new</c> response.
///
/// <para><c>ErrorCode</c>/<c>ErrorDescription</c> are how Cloudflare reports a <em>per-track</em>
/// failure — pulling a track that hasn't propagated to the publisher's session yet is the common
/// one. It arrives inside an HTTP <b>200</b> alongside a perfectly valid <c>sessionDescription</c>,
/// with <c>Mid</c> absent. This record previously declared a single <c>Error</c> property, which
/// corresponds to no field Cloudflare actually sends, so every such failure deserialised to
/// "success with a null mid" and was relayed to the client as a 200.</para>
/// </summary>
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

/// <summary>
/// Thin relay over the Cloudflare Calls (SFU) HTTP API. The server never terminates media — it only
/// proxies SDP negotiation on behalf of a client and reports back what Cloudflare said.
///
/// <para>Shared by every service that runs voice (guild channels, direct calls, Isle proximity
/// voice), which previously each carried their own copy. Register it — together with the named
/// <c>CloudflareProxy</c> HTTP client it depends on — via
/// <see cref="CloudflareServiceCollectionExtensions.AddCloudflareCalls"/>.</para>
/// </summary>
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
        var body = await res.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<CfTracksNewResponse>(body, Json);
        EnsureValidSessionDescription(result?.SessionDescription, "tracks/new", body);
        EnsureNoTrackFailures(result!, "tracks/new", body);
        return result!;
    }

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
    /// EnsureSuccessAsync above only catches Cloudflare rejecting the request outright
    /// (non-2xx). It says nothing about a 2xx response whose sessionDescription is
    /// missing or empty -which client-side surfaces only as a bare, unactionable
    /// "Failed to parse SessionDescription" from RTCPeerConnection.setRemoteDescription,
    /// with nothing in our logs to say why. Catch that here, with the raw body attached,
    /// so the next occurrence is diagnosable from this side instead of only the client's.
    /// </summary>
    private void EnsureValidSessionDescription(CfSessionDescription? desc, string operation, string rawBody)
    {
        if (desc is not null && !string.IsNullOrEmpty(desc.Sdp) && !string.IsNullOrEmpty(desc.Type)) return;

        _logger.LogError(
            "Cloudflare Calls {Operation} returned a 2xx response with a missing or empty " +
            "sessionDescription. Raw body: {Body}", operation, rawBody);
        throw new CloudflareCallsException(operation, System.Net.HttpStatusCode.OK, rawBody);
    }

    /// <summary>
    /// Turns a per-track failure into a real failure.
    ///
    /// <para><see cref="EnsureSuccessAsync"/> catches Cloudflare rejecting the whole request and
    /// <see cref="EnsureValidSessionDescription"/> catches a 2xx with no usable SDP. Neither
    /// catches the case that actually happens most: a 200, a valid session description, and one
    /// entry in <c>tracks[]</c> carrying <c>errorCode</c>/<c>errorDescription</c> in place of a
    /// <c>mid</c>. That is exactly the SFU propagation race the callers' retry loops were written
    /// for, and because it never threw, those loops never ran — the failure was relayed to the
    /// client as a successful subscribe with a mid-less track, which clients then papered over
    /// with a locally invented mid and marked the participant permanently subscribed.</para>
    ///
    /// <para>A track that came back with neither an error nor a mid is treated the same way: it is
    /// unusable to the caller either way, and silently returning it is what made this class of
    /// failure undiagnosable.</para>
    /// </summary>
    private void EnsureNoTrackFailures(CfTracksNewResponse result, string operation, string rawBody)
    {
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

        throw new CloudflareCallsException(operation, System.Net.HttpStatusCode.OK, rawBody);
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
