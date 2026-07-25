namespace Isle.Infrastructure.Sfu;

public static class SfuSocketEvents
{
    private const string Prefix = "isle.";
    public const string SubscribeMutual = Prefix + "SubscribeMutual";
    public const string PeerLeft        = Prefix + "PeerLeft";
    public const string PlayerPosition  = Prefix + "PlayerPosition";
    public const string SelfPosition    = Prefix + "SelfPosition";
    // Server lost the client's published-track record (server restart) while the client's own
    // connection stayed up. The client must re-run its Cloudflare session/track publish so peers
    // can pull it again. No-op on the client if it is already publishing / has a live track.
    public const string RepublishVoice  = Prefix + "RepublishVoice";
}