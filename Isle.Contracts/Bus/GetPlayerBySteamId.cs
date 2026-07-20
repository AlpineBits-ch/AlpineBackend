using Isle.Contracts.Dtos;

namespace Isle.Contracts.Bus;

public class GetPlayerBySteamIdRequest
{
    public string SteamId { get; set; }
}

public class GetPlayerBySteamIdResponse
{
    public PlayerDto? Player { get; set; }
}


public class GetPlayerByUserIdRequest
{
    public string UserId { get; set; }
}

public class GetPlayerByUserIdResponse
{
    public PlayerDto? Player { get; set; }
}
