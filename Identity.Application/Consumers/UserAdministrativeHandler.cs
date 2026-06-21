using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;

namespace Identity.Application.Consumers;

public class UserAdministrativeHandler
{
    public async Task<IsUserAdministrativeResponse> Handle(IsUserAdministrativeRequest request, MicroserviceContext ctx)
    {
        var user = await ctx.Users.FindAsync(request.UserId);
        return new IsUserAdministrativeResponse { IsAdministrative = user?.UserType == UserType.Admin };
    }
}