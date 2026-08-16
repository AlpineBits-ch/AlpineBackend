using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Echo.Realtime.LiveKit;

/// <summary>A LiveKit control-plane call failed.</summary>
public sealed class LiveKitControlException(
    string method, HttpStatusCode statusCode, string responseBody, Exception? inner = null)
    : Exception($"LiveKit '{method}' failed with {(int)statusCode} {statusCode}: {responseBody}", inner)
{
    public string Method { get; } = method;
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody;

    /// <summary>Whether trying again is worth anything.</summary>
    public bool IsTransient =>
        StatusCode is 0 or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)StatusCode >= 500;
}

/// <summary>One room as LiveKit reports it.</summary>
public sealed record LiveKitRoom(
    string Sid, string Name, int NumParticipants, int NumPublishers);

/// <summary>One participant as LiveKit reports it.</summary>
public sealed record LiveKitParticipant(
    string Sid, string Identity, string? Name, string State,
    IReadOnlyList<LiveKitTrack> Tracks);

/// <param name="Source">LiveKit's source tag - see <see cref="LiveKitSources"/>.</param>
public sealed record LiveKitTrack(string Sid, string? Name, string? Source, bool Muted);

/// <summary>The LiveKit room control API, over Twirp.</summary>
public sealed class LiveKitRoomClient(
    IHttpClientFactory factory, LiveKitOptions options, ILogger<LiveKitRoomClient> logger)
{
    /// <summary>The named client, registered by <see cref="LiveKitServiceCollectionExtensions"/>. No
    /// base address: each call addresses a node explicitly, because which node a room is on is the
    /// one routing decision this whole design turns on.</summary>
    public const string HttpClientName = "LiveKitControl";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Creates the room, and is a no-op when it already exists.</summary>
    public Task<LiveKitRoom> CreateRoomAsync(
        LiveKitNode node, string name, int? maxParticipants = null, CancellationToken ct = default) =>
        SendAsync<LiveKitRoom>(node, "CreateRoom", new
        {
            name,
            emptyTimeout = options.EmptyTimeoutSeconds,
            maxParticipants = maxParticipants is > 0 ? maxParticipants : null,
        }, room: name, ct);

    /// <summary>Force-ends a session.</summary>
    public Task DeleteRoomAsync(LiveKitNode node, string name, CancellationToken ct = default) =>
        SendAsync(node, "DeleteRoom", new { room = name }, room: name, ct);

    /// <summary>Every room live on one node.</summary>
    public async Task<IReadOnlyList<LiveKitRoom>> ListRoomsAsync(
        LiveKitNode node, CancellationToken ct = default)
    {
        var result = await SendAsync<ListRoomsResponse>(node, "ListRooms", new { }, room: null, ct);
        return result.Rooms ?? [];
    }

    public async Task<IReadOnlyList<LiveKitParticipant>> ListParticipantsAsync(
        LiveKitNode node, string room, CancellationToken ct = default)
    {
        var result = await SendAsync<ListParticipantsResponse>(
            node, "ListParticipants", new { room }, room, ct);
        return result.Participants ?? [];
    }

    /// <summary>Kicks one participant.</summary>
    public Task RemoveParticipantAsync(
        LiveKitNode node, string room, string identity, CancellationToken ct = default) =>
        SendAsync(node, "RemoveParticipant", new { room, identity }, room, ct);

    /// <summary>Mutes a published track at the SFU.</summary>
    public Task MuteTrackAsync(
        LiveKitNode node, string room, string identity, string trackSid, bool muted,
        CancellationToken ct = default) =>
        SendAsync(node, "MutePublishedTrack", new { room, identity, trackSid, muted }, room, ct);

    /// <summary>Changes what a participant may do without making them reconnect.</summary>
    public Task UpdatePermissionsAsync(
        LiveKitNode node, string room, string identity, LiveKitGrants grants,
        CancellationToken ct = default)
    {
        var permission = new Dictionary<string, object>
        {
            ["canPublish"] = grants.CanPublish,
            ["canSubscribe"] = grants.CanSubscribe,
            ["canPublishData"] = grants.CanPublishData,
            ["hidden"] = grants.Hidden,
        };
        if (grants.CanPublishSources is { Count: > 0 } sources)
            permission["canPublishSources"] = sources;

        return SendAsync(node, "UpdateParticipant", new { room, identity, permission }, room, ct);
    }

    /// <summary>Subscribes or unsubscribes one participant to specific tracks.</summary>
    public Task UpdateSubscriptionsAsync(
        LiveKitNode node, string room, string identity, IReadOnlyList<string> trackSids,
        bool subscribe, CancellationToken ct = default) =>
        trackSids.Count == 0
            ? Task.CompletedTask
            : SendAsync(node, "UpdateSubscriptions",
                new { room, identity, trackSids, subscribe }, room, ct);

    private Task SendAsync(
        LiveKitNode node, string method, object body, string? room, CancellationToken ct) =>
        SendAsync<JsonElement>(node, method, body, room, ct);

    private async Task<T> SendAsync<T>(
        LiveKitNode node, string method, object body, string? room, CancellationToken ct)
    {
        var http = factory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{node.ApiUrl}/twirp/livekit.RoomService/{method}")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", LiveKitToken.ForAdmin(options, room));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !ct.IsCancellationRequested)
        {
            // A dead overlay looks like this, and it is the failure the deployment notes single
            // out: the tunnel is a single point of failure for room creation and for nothing else.
            logger.LogError(ex, "LiveKit {Method} could not reach {Node} at {ApiUrl}",
                method, node.Region, node.ApiUrl);
            throw new LiveKitControlException(method, 0, ex.Message, ex);
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "LiveKit {Method} on {Node} failed with {StatusCode}: {Body}",
                    method, node.Region, (int)response.StatusCode, payload);
                throw new LiveKitControlException(method, response.StatusCode, payload);
            }

            if (typeof(T) == typeof(JsonElement) || string.IsNullOrWhiteSpace(payload))
                return default!;

            try
            {
                return JsonSerializer.Deserialize<T>(payload, Json)!;
            }
            catch (JsonException ex)
            {
                throw new LiveKitControlException(method, response.StatusCode, payload, ex);
            }
        }
    }

    private sealed record ListRoomsResponse(IReadOnlyList<LiveKitRoom>? Rooms);
    private sealed record ListParticipantsResponse(IReadOnlyList<LiveKitParticipant>? Participants);
}
