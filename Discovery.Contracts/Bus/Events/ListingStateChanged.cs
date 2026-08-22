namespace Discovery.Contracts.Bus.Events;

/// <summary>Published whenever a listing's <c>State</c> actually changes: published, unlisted, or
/// suspended. Carries only identity and the new state, the same shape as the realtime push, so a
/// consumer that wants more re-reads the listing rather than trusting a payload that could go stale
/// between publish and delivery.</summary>
public class ListingStateChanged
{
    public required string ListingId { get; init; }
    public required string GuildId { get; init; }
    public required string State { get; init; }
}
