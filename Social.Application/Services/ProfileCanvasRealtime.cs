using Echo.Realtime;
using Microsoft.AspNetCore.SignalR;
using Social.Api.Dtos.Response;
using Social.Domain.Aggregate;

namespace Social.Api.Services;

/// <summary>
/// Websocket fan-out for a canvas save. Every recipient gets their own stripped copy: one send
/// carrying the owner's full canvas would hand every subscriber the friends-only and mutuals-only
/// widgets.
/// </summary>
public sealed class ProfileCanvasRealtime(
    ProfileCanvasService canvases,
    IHubContext<EchoRealtimeHub> hub,
    ILogger<ProfileCanvasRealtime> logger)
{
    public const string EventName = "social.ProfileCanvasUpdated";

    /// <summary>How many friends one save fans out to before the rest are left to re-fetch.</summary>
    public const int MaxRecipients = 200;

    public async Task PublishAsync(Profile owner, ProfileCanvasDto canvas, CancellationToken token = default)
    {
        await SendAsync(owner.UserId, canvas);

        var friends = await canvases.FriendsOfAsync(owner.Id, MaxRecipients + 1, token);
        if (friends.Count == 0) return;

        if (friends.Count > MaxRecipients)
        {
            logger.LogWarning(
                "Canvas fan-out for {ProfileId} truncated at {Max} of {Count} friends", owner.Id, MaxRecipients, friends.Count);
            friends = friends.Take(MaxRecipients).ToList();
        }

        var mutuals = CanvasVisibility.NeedsMutualLookup(canvas.Widgets)
            ? await canvases.MutualsAmongAsync(owner, friends, token)
            : new HashSet<string>();

        foreach (var friend in friends)
        {
            var viewer = new CanvasViewer(false, true, mutuals.Contains(friend.ProfileId));
            await SendAsync(friend.UserId, CanvasVisibility.Strip(canvas, viewer));
        }
    }

    private Task SendAsync(string userId, ProfileCanvasDto canvas) =>
        hub.Clients.User(userId).SendAsync(EventName, new ProfileCanvasUpdatedPayload
        {
            ProfileId = canvas.ProfileId,
            Canvas = canvas,
        });
}
