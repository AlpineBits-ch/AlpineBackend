using Echo.Realtime.Sfu;

namespace Echo.Voice.Transport;

/// <summary><see cref="IVoiceMediaTransport"/> over Cloudflare Realtime.</summary>
public sealed class CloudflareMediaTransport(CloudflareService cloudflare) : IVoiceMediaTransport
{
    public string Backend => "cloudflare";

    public async Task<string> CreateSessionAsync(CancellationToken ct = default)
    {
        try
        {
            return await cloudflare.CreateSessionAsync(ct);
        }
        catch (CloudflareCallsException ex)
        {
            throw Translate(ex);
        }
    }

    public Task<VoiceNegotiateResponse> PublishAsync(
        string mediaSessionId, VoiceNegotiateRequest request, CancellationToken ct = default) =>
        NegotiateAsync(mediaSessionId, request, retryTransient: false, ct);

    public Task<VoiceNegotiateResponse> SubscribeAsync(
        string mediaSessionId, VoiceNegotiateRequest request, CancellationToken ct = default) =>
        NegotiateAsync(mediaSessionId, request, retryTransient: true, ct);

    private async Task<VoiceNegotiateResponse> NegotiateAsync(
        string mediaSessionId, VoiceNegotiateRequest request, bool retryTransient, CancellationToken ct)
    {
        var cfRequest = new CfTracksNewRequest(
            new CfSessionDescription(request.SessionDescription.Type, request.SessionDescription.Sdp),
            request.Tracks.Select(ToCloudflare).ToList());

        try
        {
            var result = retryTransient
                ? await cloudflare.SubscribeTracksAsync(mediaSessionId, cfRequest, ct)
                : await cloudflare.TracksNewAsync(mediaSessionId, cfRequest, ct);

            return new VoiceNegotiateResponse(
                new VoiceSessionDescription(result.SessionDescription.Type, result.SessionDescription.Sdp),
                result.Tracks.Select(FromCloudflare).ToList(),
                result.RequiresImmediateRenegotiation);
        }
        catch (CloudflareCallsException ex)
        {
            throw Translate(ex);
        }
    }

    public async Task<VoiceSessionDescription> RenegotiateAsync(
        string mediaSessionId, VoiceSessionDescription offer, CancellationToken ct = default)
    {
        try
        {
            var result = await cloudflare.RenegotiateAsync(
                mediaSessionId,
                new CfRenegotiateRequest(new CfSessionDescription(offer.Type, offer.Sdp)),
                ct);

            return new VoiceSessionDescription(
                result.SessionDescription.Type, result.SessionDescription.Sdp);
        }
        catch (CloudflareCallsException ex)
        {
            throw Translate(ex);
        }
    }

    public async Task CloseTracksAsync(
        string mediaSessionId, IEnumerable<string> trackNames, CancellationToken ct = default)
    {
        try
        {
            await cloudflare.CloseTracksAsync(mediaSessionId, trackNames, ct);
        }
        catch (CloudflareCallsException ex)
        {
            throw Translate(ex);
        }
    }

    /// <summary>Cloudflare calls a published track "local" and a pulled one "remote", which
    /// describes where the media sits rather than what the caller is doing with it.</summary>
    private static CfTrackNew ToCloudflare(VoiceTrackRef track) => new(
        Location: track.Direction == VoiceTrackDirection.Subscribe ? "remote" : "local",
        Mid: track.Mid,
        TrackName: track.TrackName,
        SessionId: track.MediaSessionId);

    private static VoiceTrackResult FromCloudflare(CfTrackResult track) => new(
        track.Mid,
        track.TrackName,
        track.SessionId,
        track.Location == "remote" ? VoiceTrackDirection.Subscribe : VoiceTrackDirection.Publish);

    /// <summary>Cloudflare's failure vocabulary, mapped onto ours.</summary>
    private static VoiceMediaException Translate(CloudflareCallsException ex)
    {
        var failure = ex.TrackErrorCodes.Any(c => c.Contains("not_found_track", StringComparison.OrdinalIgnoreCase))
            ? VoiceMediaFailure.TrackNotFound
            : ex.ErrorCode is { } code
              && code.Contains(CloudflareService.SessionErrorCode, StringComparison.OrdinalIgnoreCase)
                ? VoiceMediaFailure.SessionGone
                : (int)ex.StatusCode >= 500
                    ? VoiceMediaFailure.Unavailable
                    : VoiceMediaFailure.Rejected;

        return new VoiceMediaException(ex.Operation, failure, ex.ResponseBody, ex);
    }
}
