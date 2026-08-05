namespace Isle.Contracts.Events.Voice;

// PlayerId and OtherId (both userIds) came within earshot of each other - their 3x3 voice
// blocks now overlap. Drives a mutual SubscribeMutual + an initial position seed.
public record PeerBecameAudibleEvent(string PlayerId, string OtherId);
