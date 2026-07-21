namespace Isle.Contracts.Events.Voice;

// PlayerId and OtherId (both userIds) moved out of earshot of each other (or one left voice).
// Drives a mutual teardown — both sides are told to drop the other.
public record PeerBecameInaudibleEvent(string PlayerId, string OtherId);
