using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

public class GetDeviceTokenHandler
{
    public async Task<GetDeviceTokenForUserIdResponse> Handle(GetDeviceTokenForUserIdRequest request, MicroserviceContext ctx)
    {
        var tokens = await ctx.UserDeviceTokens.Where(t => request.UserIds.Contains(t.UserId)).Select(t => t.Token).ToListAsync();
        return new GetDeviceTokenForUserIdResponse { Tokens = tokens };
    }
}