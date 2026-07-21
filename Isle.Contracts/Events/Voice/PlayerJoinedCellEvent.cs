using Isle.Api.Voice;

namespace Isle.Contracts.Events.Voice;

public record PlayerJoinedCellEvent(string PlayerId, MapCell Cell);
