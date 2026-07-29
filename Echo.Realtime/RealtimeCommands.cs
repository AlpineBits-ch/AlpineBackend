namespace Echo.Realtime;

// Contracts forwarded from the single gateway-terminated hub (EchoRealtimeHub) to the
// owning microservice over Wolverine/RabbitMQ. Every command carries a server-authoritative
// UserId that the hub sets from Context.UserIdentifier — clients never supply it (any value a
// client sends is overwritten in the hub before the command is dispatched).

// ---- Connection lifecycle (published; handled by every interested service) ----

public record UserConnected(string UserId, string? DeviceId = null);

public record UserDisconnected(string UserId, string? DeviceId = null);

public record PresenceHeartbeat(string UserId);

// ---- Conversation / DM (Messaging service) ----

public record StartConversationTypingCommand(string UserId, string ConversationId);

/// <summary>Field name <c>Id</c> mirrors the legacy UpdateReadReceiptDto wire shape.</summary>
public record UpdateConversationReadCommand(string UserId, string ConversationId, string Id);

// ---- 1:1 call voice (Messaging service) ----

public record CallMuteCommand(string UserId, string CallId, bool IsMuted);

public record CallCameraCommand(string UserId, string CallId, bool IsCameraOn);

public record CallSpeakingCommand(string UserId, string CallId, bool IsSpeaking);

public record CallScreenShareStartCommand(string UserId, string CallId, string ShareId, string TrackName);

public record CallScreenShareStopCommand(string UserId, string CallId, string ShareId);

// ---- Guild text (Guild service) ----

public record StartGuildTypingCommand(string UserId, string ChannelId);

/// <summary>Field name <c>Id</c> mirrors the legacy UpdateLastReadMessageByChannelDto wire shape.</summary>
public record UpdateGuildReadCommand(string UserId, string ChannelId, string Id);

// ---- Guild voice (Guild service) ----

public record GuildVoiceHeartbeatCommand(string UserId);

public record GuildVoiceMuteCommand(string UserId, string ChannelId, bool IsMuted);

public record GuildVoiceDeafenCommand(string UserId, string ChannelId, bool IsDeafened);

public record GuildVoiceCameraCommand(string UserId, string ChannelId, bool IsCameraOn);

public record GuildVoiceScreenShareStartCommand(string UserId, string ChannelId, string ShareId, string TrackName);

public record GuildVoiceScreenShareStopCommand(string UserId, string ChannelId, string ShareId);

public record GuildVoiceServerMuteCommand(string UserId, string ChannelId, string TargetUserId, bool IsMuted);

public record GuildVoiceServerDeafenCommand(string UserId, string ChannelId, string TargetUserId, bool IsDeafened);

public record GuildVoiceMoveUserCommand(string UserId, string ChannelId, string TargetUserId, string TargetChannelId);
