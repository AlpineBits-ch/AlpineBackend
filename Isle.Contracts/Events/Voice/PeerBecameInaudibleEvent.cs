namespace Isle.Contracts.Events.Voice;

// PlayerId and OtherId (both userIds) moved out of earshot of each other (or one left voice).
public record PeerBecameInaudibleEvent(string PlayerId, string OtherId);
