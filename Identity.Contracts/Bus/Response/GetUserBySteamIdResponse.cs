using Identity.Contracts.Dto.Response;

namespace Identity.Contracts.Bus.Response;

public class GetUserBySteamIdResponse
{
    public ApplicationUserDto? User { get; set; }
}