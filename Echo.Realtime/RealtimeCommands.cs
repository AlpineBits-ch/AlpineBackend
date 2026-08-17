namespace Echo.Realtime;

// Contracts forwarded from the single gateway-terminated hub (EchoRealtimeHub) to the owning
// microservice over Wolverine/RabbitMQ.

// ---- Connection lifecycle (published; handled by every interested service) ----

public record UserConnected(string UserId, string? DeviceId = null);

/// <param name="ServerStopping">
/// True when this socket closed because the gateway is shutting down, not because anything happened
/// to the client.
/// </param>
public record UserDisconnected(string UserId, string? DeviceId = null, bool ServerStopping = false);

public record PresenceHeartbeat(string UserId);

// ---- Conversation / DM (Messaging service) ----

public record StartConversationTypingCommand(string UserId, string ConversationId);

/// <summary>Field name <c>Id</c> mirrors the legacy UpdateReadReceiptDto wire shape.</summary>
public record UpdateConversationReadCommand(string UserId, string ConversationId, string Id);

// ---- 1:1 call voice (Messaging service) ----

public record CallMuteCommand(string UserId, string CallId, bool IsMuted);

public record CallCameraCommand(string UserId, string CallId, bool IsCameraOn);

public record CallSpeakingCommand(string UserId, string CallId, bool IsSpeaking);

public record CallScreenShareStartCommand(string UserId, string CallId, string ShareId);

public record CallScreenShareStopCommand(string UserId, string CallId, string ShareId);

// ---- Guild text (Guild service) ----

public record StartGuildTypingCommand(string UserId, string ChannelId);

/// <summary>Field name <c>Id</c> mirrors the legacy UpdateLastReadMessageByChannelDto wire shape.</summary>
public record UpdateGuildReadCommand(string UserId, string ChannelId, string Id);

// ---- Guild voice (Guild service) ----

/// <summary>What a client asserts about itself on every beat.</summary>
public record VoiceHeartbeatState(
    string? KnownInstanceId,
    long KnownVersion,
    string? MediaSessionId,
    string? AudioTrackName);

// Two records rather than one carrying a room kind, purely because Wolverine routes on the concrete
// message type and these two go to different services.

// DeviceId is server-authoritative, taken from the connection rather than the payload, and is what
// the liveness claim is keyed on - see VoiceReconciler.LivenessKey.
public record GuildVoiceReconcileCommand(
    string UserId, string ChannelId, VoiceHeartbeatState State, string? DeviceId = null);

public record CallVoiceReconcileCommand(
    string UserId, string CallId, VoiceHeartbeatState State, string? DeviceId = null);

public record GuildVoiceMuteCommand(string UserId, string ChannelId, bool IsMuted);

public record GuildVoiceDeafenCommand(string UserId, string ChannelId, bool IsDeafened);

public record GuildVoiceCameraCommand(string UserId, string ChannelId, bool IsCameraOn);

/// <summary>
/// The guild counterpart of <see cref="CallSpeakingCommand"/>, and the only input active-speaker
/// subscription has.
/// </summary>
public record GuildVoiceSpeakingCommand(string UserId, string ChannelId, bool IsSpeaking);

public record GuildVoiceScreenShareStartCommand(string UserId, string ChannelId, string ShareId);

public record GuildVoiceScreenShareStopCommand(string UserId, string ChannelId, string ShareId);

public record GuildVoiceServerMuteCommand(string UserId, string ChannelId, string TargetUserId, bool IsMuted);

public record GuildVoiceServerDeafenCommand(string UserId, string ChannelId, string TargetUserId, bool IsDeafened);

public record GuildVoiceMoveUserCommand(string UserId, string ChannelId, string TargetUserId, string TargetChannelId);
