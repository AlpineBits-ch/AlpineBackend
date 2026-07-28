using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

public class GetVoipTokenHandler
{
    public async Task<GetVoipTokenForUserIdResponse> Handle(GetVoipTokenForUserIdRequest request, MicroserviceContext ctx)
    {
        var tokens = await ctx.UserVoipTokens.Where(t => request.UserIds.Contains(t.UserId)).Select(t => t.Token).ToListAsync();
        return new GetVoipTokenForUserIdResponse { Tokens = tokens };
    }
}
