using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Dto.Response;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Handlers;

public class GetUserByIdHandler(MicroserviceContext context)
{
    public async Task<GetUserByIdResponse> Handle(GetUserByIdRequest request)
    {
        var user = await context.Users.FirstOrDefaultAsync(u => u.Id == request.UserId);

        if (user == null) return new GetUserByIdResponse();
        return new GetUserByIdResponse
        {
            User = new ApplicationUserDto()
            {
                Id = user.Id,
                Email = user.Email ?? "no_email_found$",
                SteamId = user.SteamId,
                UserName = user.UserName,
                IsBot = user.UserType == UserType.Bot,
                EmailConfirmed = user.EmailConfirmed,
                CreatedAt = user.CreatedAt,
            }
        };
    }
}
