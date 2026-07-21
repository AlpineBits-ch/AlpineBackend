using Isle.Api.Voice;

namespace Isle.Domain.Events.Voice;


public record UpdatePlayerPosition(string PlayerId, float WorldX, float WorldY);
public record RemovePlayer(string PlayerId);

public record PlayerJoinedCell(string PlayerId, MapCell Cell);
public record PlayerLeftCell(string PlayerId, MapCell Cell);