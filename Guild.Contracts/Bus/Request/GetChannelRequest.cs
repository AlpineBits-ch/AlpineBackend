namespace Guild.Contracts.Bus.Request;

/// <summary>Resolves a single channel by id regardless of guild - backs the Discord-compat
/// GET /channels/{id} endpoint, which real bot libraries call to explicitly re-fetch/verify a
/// channel (e.g. one configured via an env var) rather than trusting only the GUILD_CREATE cache.</summary>
public class GetChannelRequest
{
    public string ChannelId { get; set; }
}
