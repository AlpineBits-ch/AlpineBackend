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

        // A suspended or deleted staff account is not staff.
        var eligible = user is not null && user.Status == UserStatus.Active;

        var role = eligible
            ? user!.UserType switch
            {
                UserType.Admin => "Admin",
                UserType.Moderator => "Moderator",
                _ => "None",
            }
            : "None";

        return new IsUserAdministrativeResponse
        {
            // Unchanged meaning: Admin only. See the remarks on the field.
            IsAdministrative = role == "Admin",
            Role = role,
            IsStaff = role is "Admin" or "Moderator",
            UserName = user?.UserName,
        };
    }
}
