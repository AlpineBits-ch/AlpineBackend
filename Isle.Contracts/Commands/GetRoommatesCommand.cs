namespace Isle.Contracts.Commands;

public record GetRoommatesCommand(string PlayerId);

public record RoommatesCommandResponse(string PlayerId, IReadOnlyCollection<string> Roommates);