using Identity.Contracts.Dto.Response;

namespace Identity.Contracts.Bus.Response;

public class GetUserByIdResponse
{
    public ApplicationUserDto? User { get; set; }
}
