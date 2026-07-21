namespace Isle.Contracts.Commands;

public record GetRoommates(string PlayerId);

public record RoommatesResponse(string PlayerId, IReadOnlyCollection<string> Roommates);