using System.Security.Cryptography;
using Identity.Contracts.Bus.Commands;
using OpenIddict.Abstractions;

namespace Identity.Application.Commands;

public class ResetBotSecretCommandHandler
{
    public static async Task<ResetBotSecretResponse> Handle(ResetBotSecretCommand command, IOpenIddictApplicationManager appManager)
    {
        var application = await appManager.FindByClientIdAsync(command.BotUserId);
        if (application is null) return new ResetBotSecretResponse { Found = false };

        var descriptor = new OpenIddictApplicationDescriptor();
        await appManager.PopulateAsync(descriptor, application);

        var clientSecret = RandomNumberGenerator.GetHexString(64);
        descriptor.ClientSecret = clientSecret;

        await appManager.UpdateAsync(application, descriptor);

        return new ResetBotSecretResponse { Found = true, ClientSecret = clientSecret };
    }
}
