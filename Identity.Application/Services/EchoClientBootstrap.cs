using Identity.Application.Services.Qr;
using Identity.Application.Services.Steam;
using OpenIddict.Abstractions;

namespace Identity.Application.Services;

/// <summary>
/// Keeps the first-party <c>echo</c> OpenIddict client in step with the grants this service
/// actually supports.
/// </summary>
public static class EchoClientBootstrap
{
    public const string ClientId = "echo";

    /// <summary>Every permission the first-party client needs, in one place.</summary>
    public static readonly string[] Permissions =
    [
        OpenIddictConstants.Permissions.Endpoints.Token,
        OpenIddictConstants.Permissions.GrantTypes.Password,
        OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
        OpenIddictConstants.Permissions.Prefixes.GrantType + SteamOpenIdService.SteamGrantType,
        OpenIddictConstants.Permissions.Prefixes.GrantType + QrLoginService.QrGrantType,
        OpenIddictConstants.Permissions.Scopes.Email,
        OpenIddictConstants.Permissions.Scopes.Profile,
        OpenIddictConstants.Permissions.Scopes.Roles,
    ];

    /// <summary>
    /// Creates the client if it is absent, and otherwise adds any permission it is missing.
    /// </summary>
    /// <returns>True if the row was created or changed.</returns>
    public static async Task<bool> EnsureAsync(
        IOpenIddictApplicationManager manager, CancellationToken ct = default)
    {
        var application = await manager.FindByClientIdAsync(ClientId, ct);

        if (application is null)
        {
            var created = new OpenIddictApplicationDescriptor
            {
                ClientId = ClientId,
                DisplayName = "Echo Application",
            };

            created.Permissions.UnionWith(Permissions);
            await manager.CreateAsync(created, ct);
            return true;
        }

        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, application, ct);

        // Union, never subtract.
        var added = false;
        foreach (var permission in Permissions)
        {
            added |= descriptor.Permissions.Add(permission);
        }

        if (!added) return false;

        await manager.UpdateAsync(application, descriptor, ct);
        return true;
    }
}
