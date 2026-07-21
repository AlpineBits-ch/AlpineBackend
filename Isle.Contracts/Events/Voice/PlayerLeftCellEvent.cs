using Isle.Api.Voice;

namespace Isle.Contracts.Events.Voice;

public record PlayerLeftCellEvent(string PlayerId, MapCell Cell);