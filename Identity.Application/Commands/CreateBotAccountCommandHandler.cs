using System.Security.Cryptography;
using Identity.Contracts.Bus.Commands;
using Identity.Domain.Aggregates;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Identity.Application.Commands;

public class CreateBotAccountCommandHandler
{
    public static async Task<CreateBotAccountResponse> Handle(CreateBotAccountCommand command, MicroserviceContext ctx, IOpenIddictApplicationManager appManager)
    {
        var existing = await ctx.Users.FirstOrDefaultAsync(u => u.Id == command.BotUserId);
        if (existing != null)
        {
            return new CreateBotAccountResponse { BotUserId = command.BotUserId, ClientSecret = string.Empty };
        }

        var bot = ApplicationUser.CreateBot(command.BotUserId, command.Name);
        ctx.Users.Add(bot);

        var clientSecret = RandomNumberGenerator.GetHexString(64);
        await appManager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = command.BotUserId,
            ClientSecret = clientSecret,
            ClientType = OpenIddictConstants.ClientTypes.Confidential,
            DisplayName = command.Name,
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.ClientCredentials,
            }
        });

        return new CreateBotAccountResponse { BotUserId = command.BotUserId, ClientSecret = clientSecret };
    }
}
