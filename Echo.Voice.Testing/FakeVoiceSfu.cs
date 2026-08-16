using Echo.Voice.Rooms;
using Echo.Voice.Transport;

namespace Echo.Voice.Testing;

/// <summary>
/// An <see cref="IVoiceSfu"/> that records what it was asked for and hands back a plausible
/// connection.
/// </summary>
public sealed class FakeVoiceSfu : IVoiceSfu
{
    public sealed record Connect(
        VoiceRoomKey Key, string Identity, string? DisplayName, VoiceMediaRights Rights,
        int? MaxParticipants);

    public sealed record RightsChange(VoiceRoomKey Key, string Identity, VoiceMediaRights Rights);

    public List<Connect> Connections { get; } = [];
    public List<RightsChange> RightsChanges { get; } = [];
    public List<(VoiceRoomKey Key, string Identity)> Disconnects { get; } = [];
    public List<VoiceRoomKey> Ended { get; } = [];

    /// <summary>What <see cref="ListParticipantsAsync"/> answers.</summary>
    public List<VoiceSfuParticipant> Participants { get; } = [];

    /// <summary>Set to make the next control-plane call fail the way an unreachable overlay does.
    /// Callers must answer that with a retry, not a teardown.</summary>
    public VoiceMediaFailure? FailWith { get; set; }

    public string Backend => "livekit";

    public bool IsConfigured { get; set; } = true;

    public string SignalingUrl { get; set; } = "wss://sfu-test.venta.gg";

    public Task<VoiceConnection> ConnectAsync(
        VoiceRoomKey key, string identity, string? displayName, VoiceMediaRights rights,
        int? maxParticipants = null, CancellationToken ct = default)
    {
        Fail("connect");
        Connections.Add(new Connect(key, identity, displayName, rights, maxParticipants));

        return Task.FromResult(new VoiceConnection(
            Backend, SignalingUrl, $"token-for-{identity}", $"{key.Kind}-{key.Id}", identity,
            DateTimeOffset.UtcNow.AddMinutes(10)));
    }

    public Task<bool> UpdateRightsAsync(
        VoiceRoomKey key, string identity, VoiceMediaRights rights, CancellationToken ct = default)
    {
        Fail("UpdateParticipant");
        RightsChanges.Add(new RightsChange(key, identity, rights));
        return Task.FromResult(true);
    }

    public Task DisconnectAsync(VoiceRoomKey key, string identity, CancellationToken ct = default)
    {
        Disconnects.Add((key, identity));
        return Task.CompletedTask;
    }

    public Task EndAsync(VoiceRoomKey key, CancellationToken ct = default)
    {
        Ended.Add(key);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VoiceSfuParticipant>> ListParticipantsAsync(
        VoiceRoomKey key, CancellationToken ct = default)
    {
        Fail("ListParticipants");
        return Task.FromResult<IReadOnlyList<VoiceSfuParticipant>>(Participants);
    }

    private void Fail(string operation)
    {
        if (FailWith is { } failure)
            throw new VoiceMediaException(operation, failure, "scripted failure");
    }
}
