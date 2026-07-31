using Echo.Realtime.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Echo.Realtime;

/// <summary>
/// The single per-user realtime connection, terminated on the Echo (YARP) gateway pod.
/// </summary>
[Authorize]
public class EchoRealtimeHub(ILogger<EchoRealtimeHub> logger, IMessageBus bus) : Hub
{
    /// <summary>How often (seconds) a live connection republishes a presence heartbeat.</summary>
    private const long PresenceHeartbeatIntervalSeconds = 30;

    /// <summary>Device id used when a client connects without one (older builds) - keeps every
    /// one of that user's un-tagged sessions bucketed together instead of failing outright. Shared
    /// with the REST side so the hub and the controllers bucket un-tagged clients identically.</summary>
    private const string DefaultDeviceId = DeviceIdentity.DefaultDeviceId;

    private string Uid() =>
        Context.UserIdentifier ?? throw new HubException("User not authenticated");

    /// <summary>SignalR group name for one specific device of one specific user.</summary>
    public static string DeviceGroup(string userId, string deviceId) => $"device:{userId}:{deviceId}";

    private string ResolveDeviceId()
    {
        var deviceId = Context.GetHttpContext()?.Request.Query[DeviceIdentity.QueryName].ToString();
        return string.IsNullOrWhiteSpace(deviceId) ? DefaultDeviceId : deviceId;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrWhiteSpace(userId))
            throw new HubException("User not authenticated");

        var deviceId = ResolveDeviceId();
        Context.Items["DeviceId"] = deviceId;

        logger.LogInformation("Realtime client connected, connection {ConnectionId}, user {UserId}, device {DeviceId}",
            Context.ConnectionId, userId, deviceId);

        await Groups.AddToGroupAsync(Context.ConnectionId, DeviceGroup(userId, deviceId));

        await bus.PublishAsync(new UserConnected(userId, deviceId));

        // Keep presence fresh while the socket lives, throttled so we don't publish on every pulse.
        var heartbeat = Context.Features.Get<IConnectionHeartbeatFeature>();
        if (heartbeat is not null)
        {
            var lastBeat = new long[1];
            heartbeat.OnHeartbeat(state =>
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (now - Interlocked.Read(ref lastBeat[0]) < PresenceHeartbeatIntervalSeconds) return;
                Interlocked.Exchange(ref lastBeat[0], now);
                _ = bus.PublishAsync(new PresenceHeartbeat((string)state));
            }, userId);
        }
        else
        {
            logger.LogWarning("Connection heartbeat feature not found for user {UserId}", userId);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.UserIdentifier;
        var deviceId = Context.Items.TryGetValue("DeviceId", out var value) ? value as string : null;
        logger.LogInformation("Realtime client disconnected, connection {ConnectionId}, user {UserId}, device {DeviceId}",
            Context.ConnectionId, userId, deviceId);

        if (!string.IsNullOrWhiteSpace(userId))
            await bus.PublishAsync(new UserDisconnected(userId, deviceId));

        await base.OnDisconnectedAsync(exception);
    }

    // ---- Conversation / DM ----

    [HubMethodName("conversation.StartTyping")]
    public Task ConversationStartTyping(string conversationId) =>
        bus.SendAsync(new StartConversationTypingCommand(Uid(), conversationId)).AsTask();

    [HubMethodName("conversation.UpdateLastRead")]
    public Task ConversationUpdateLastRead(UpdateConversationReadCommand cmd) =>
        bus.SendAsync(cmd with { UserId = Uid() }).AsTask();

    // ---- 1:1 call voice ----

    [HubMethodName("call.MuteChanged")]
    public Task CallMuteChanged(CallMuteCommand cmd) =>
        bus.SendAsync(cmd with { UserId = Uid() }).AsTask();

    [HubMethodName("call.CameraChanged")]
    public Task CallCameraChanged(CallCameraCommand cmd) =>
        bus.SendAsync(cmd with { UserId = Uid() }).AsTask();

    [HubMethodName("call.SpeakingChanged")]
    public Task CallSpeakingChanged(CallSpeakingCommand cmd) =>
        bus.SendAsync(cmd with { UserId = Uid() }).AsTask();

    [HubMethodName("call.ScreenShareStarted")]
    public Task CallScreenShareStarted(CallScreenShareStartCommand cmd) =>
        bus.SendAsync(cmd with { UserId = Uid() }).AsTask();

    [HubMethodName("call.ScreenShareStopped")]
    public Task CallScreenShareStopped(CallScreenShareStopCommand cmd) =>
        bus.SendAsync(cmd with { UserId = Uid() }).AsTask();

    // ---- Guild text ----

    [HubMethodName("guild.StartTyping")]
    public Task GuildStartTyping(string channelId) =>
        bus.SendAsync(new StartGuildTypingCommand(Uid(), channelId)).AsTask();

    [HubMethodName("guild.UpdateLastRead")]
    public Task GuildUpdateLastRead(UpdateGuildReadCommand cmd) =>
        bus.SendAsync(cmd with { UserId = Uid() }).AsTask();

    // ---- Guild voice ----

    [HubMethodName("guild.voice.Heartbeat")]
    public Task GuildVoiceHeartbeat() =>
        bus.SendAsync(new GuildVoiceHeartbeatCommand(Uid())).AsTask();

    [HubMethodName("guild.voice.MuteChanged")]
    public Task GuildVoiceMuteChanged(GuildVoiceMuteCommand cmd) =>
        bus.SendAsync(cmd with { UserId = Uid() }).AsTask();

    [HubMethodName("guild.voice.DeafenChanged")]
    public Task GuildVoiceDeafenChanged(GuildVoiceDeafenCommand cmd) =>
        bus.SendAsync(cmd with { UserId = Uid() }).AsTask();

    [HubMethodName("guild.voice.CameraChanged")]
    public Task GuildVoiceCameraChanged(GuildVoiceCameraCommand cmd) =>
        bus.SendAsync(cmd with { UserId = Uid() }).AsTask();

    [HubMethodName("guild.voice.ScreenShareStarted")]
    public Task GuildVoiceScreenShareStarted(GuildVoiceScreenShareStartCommand cmd) =>
        bus.SendAsync(cmd with { UserId = Uid() }).AsTask();

    [HubMethodName("guild.voice.ScreenShareStopped")]
    public Task GuildVoiceScreenShareStopped(GuildVoiceScreenShareStopCommand cmd) =>
        bus.SendAsync(cmd with { UserId = Uid() }).AsTask();

    [HubMethodName("guild.voice.ServerMute")]
    public Task GuildVoiceServerMute(GuildVoiceServerMuteCommand cmd) =>
        bus.SendAsync(cmd with { UserId = Uid() }).AsTask();

    [HubMethodName("guild.voice.ServerDeafen")]
    public Task GuildVoiceServerDeafen(GuildVoiceServerDeafenCommand cmd) =>
        bus.SendAsync(cmd with { UserId = Uid() }).AsTask();

    [HubMethodName("guild.voice.MoveUser")]
    public Task GuildVoiceMoveUser(GuildVoiceMoveUserCommand cmd) =>
        bus.SendAsync(cmd with { UserId = Uid() }).AsTask();
}
