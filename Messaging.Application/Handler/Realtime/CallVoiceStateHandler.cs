using Echo.Realtime;
using Echo.Realtime.Caching;
using Echo.Voice.Rooms;

namespace Messaging.Application.Handler.Realtime;

/// <summary>Call voice state changes arriving over the hub.</summary>
public class CallVoiceStateHandler
{
    private static VoiceRoomKey Room(string callId) => VoiceRoomKey.Call(callId);

    public Task Handle(CallMuteCommand cmd, VoiceRoomService voice) =>
        voice.SetMuteAsync(Room(cmd.CallId), cmd.UserId, cmd.IsMuted, serverForced: false);

    public Task Handle(CallCameraCommand cmd, VoiceRoomService voice) =>
        voice.SetCameraAsync(Room(cmd.CallId), cmd.UserId, cmd.IsCameraOn);

    public Task Handle(CallSpeakingCommand cmd, VoiceRoomService voice) =>
        voice.SetSpeakingAsync(Room(cmd.CallId), cmd.UserId, cmd.IsSpeaking);

    public Task Handle(CallScreenShareStartCommand cmd, VoiceRoomService voice) =>
        voice.SetStreamingAsync(Room(cmd.CallId), cmd.UserId, isStreaming: true, cmd.ShareId);

    public async Task Handle(CallScreenShareStopCommand cmd, VoiceRoomService voice, StreamViewerStore viewers)
    {
        var room = await voice.SetStreamingAsync(
            Room(cmd.CallId), cmd.UserId, isStreaming: false, cmd.ShareId);

        // Gated on the result: a non-participant must not be able to clear a call's viewer table,
        // which the handler this replaces allowed.
        if (room is null) return;

        // The audience of a share that stopped is not empty, it is undefined - dropped outright so
        // a later share reusing the id cannot inherit it.
        await viewers.RemoveShareAsync(Room(cmd.CallId).ViewerScope, cmd.ShareId);
    }

    /// <summary>The state-asserting heartbeat.</summary>
    public Task Handle(CallVoiceReconcileCommand message, VoiceReconciler reconciler) =>
        reconciler.HeartbeatAsync(
            message.UserId, Room(message.CallId),
            message.State.KnownInstanceId, message.State.KnownVersion,
            message.State.MediaSessionId, message.State.AudioTrackName,
            message.DeviceId);
}
