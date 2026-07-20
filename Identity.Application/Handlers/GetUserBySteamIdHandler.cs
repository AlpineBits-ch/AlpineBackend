using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Dto.Response;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Handlers;

public class GetUserBySteamIdHandler(MicroserviceContext context)
{
    public async Task<GetUserBySteamIdResponse> Handle(GetUserBySteamIdRequest request)
    {
        var user = await context.Users.FirstOrDefaultAsync(u => u.SteamId == request.SteamId);
        
        if(user == null) return new GetUserBySteamIdResponse();
        return new GetUserBySteamIdResponse { User = new ApplicationUserDto()
        {
            Id = user.Id,
            Email = user.Email ?? "no_email_found$",
            SteamId = user.SteamId
        } };
    }
}