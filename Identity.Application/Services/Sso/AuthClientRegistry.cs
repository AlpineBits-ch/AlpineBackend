using System.Text.Json;
using AppEnvironment;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Identity.Application.Services.Sso;

/// <summary>One entry of the <c>AUTH_CLIENTS</c> allowlist. See docs/specs/sso.md §7.</summary>
public sealed class AuthClientDefinition
{
    public string ClientId { get; init; } = string.Empty;

    public string? DisplayName { get; init; }

    /// <summary>Shown on the consent and sign-in screens.</summary>
    public string? LogoUri { get; init; }

    public string[] RedirectUris { get; init; } = [];

    public string[] PostLogoutRedirectUris { get; init; } = [];

    public string[] Scopes { get; init; } =
    [
        OpenIddictConstants.Scopes.OpenId,
        OpenIddictConstants.Scopes.Profile,
        OpenIddictConstants.Scopes.Email,
        OpenIddictConstants.Scopes.OfflineAccess,
    ];

    /// <summary>Pre-authorized: no consent screen. See <see cref="AuthClientRegistry"/>.</summary>
    public bool FirstParty { get; init; }

    /// <summary>No client secret - a browser app. PKCE is required either way.</summary>
    public bool Public { get; init; } = true;
}

/// <summary>
/// Reconciles the <c>AUTH_CLIENTS</c> configuration allowlist into OpenIddict's existing EF tables
/// at startup.
/// </summary>
public static class AuthClientRegistry
{
    /// <summary>Marks a row as ours, so reconciliation only ever disables clients it created.</summary>
    private const string ManagedProperty = "venta_managed";

    private const string LogoProperty = "logo_uri";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Parses the configured JSON.</summary>
    public static IReadOnlyList<AuthClientDefinition> Parse(string? json, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            var parsed = JsonSerializer.Deserialize<AuthClientDefinition[]>(json, Json) ?? [];

            return [.. parsed.Where(definition => IsUsable(definition, logger))];
        }
        catch (JsonException exception)
        {
            logger.LogError(exception,
                "AUTH_CLIENTS is not valid JSON. No SSO clients will be registered; existing rows are left alone.");
            return [];
        }
    }

    private static bool IsUsable(AuthClientDefinition definition, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(definition.ClientId))
        {
            logger.LogError("An AUTH_CLIENTS entry has no clientId and was ignored.");
            return false;
        }

        if (definition.RedirectUris.Length == 0)
        {
            logger.LogError("AUTH_CLIENTS entry {ClientId} has no redirectUris and was ignored: "
                            + "the authorization code flow has nowhere to send the browser.", definition.ClientId);
            return false;
        }

        var invalid = definition.RedirectUris.Concat(definition.PostLogoutRedirectUris)
            .FirstOrDefault(uri => !Uri.TryCreate(uri, UriKind.Absolute, out _));

        if (invalid is not null)
        {
            logger.LogError("AUTH_CLIENTS entry {ClientId} was ignored: {Uri} is not an absolute URI.",
                definition.ClientId, invalid);
            return false;
        }

        return true;
    }

    public static async Task ReconcileAsync(
        IOpenIddictApplicationManager manager, IReadOnlyList<AuthClientDefinition> definitions,
        ILogger logger, CancellationToken ct = default)
    {
        var configured = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in definitions)
        {
            var secret = definition.Public ? null : Env.AuthConfiguration.ClientSecret(definition.ClientId);

            if (!definition.Public && string.IsNullOrWhiteSpace(secret))
            {
                logger.LogError("AUTH_CLIENTS entry {ClientId} is confidential but {Variable} is not set; skipped.",
                    definition.ClientId, AuthConfiguration.ClientSecretVariable(definition.ClientId));
                continue;
            }

            var existing = await manager.FindByClientIdAsync(definition.ClientId, ct);

            var descriptor = new OpenIddictApplicationDescriptor();
            if (existing is not null) await manager.PopulateAsync(descriptor, existing, ct);

            Apply(descriptor, definition, secret);

            if (existing is null)
            {
                await manager.CreateAsync(descriptor, ct);
                logger.LogInformation("Registered SSO client {ClientId}", definition.ClientId);
            }
            else
            {
                await manager.UpdateAsync(existing, descriptor, ct);
                logger.LogInformation("Updated SSO client {ClientId}", definition.ClientId);
            }

            configured.Add(definition.ClientId);
        }

        await DisableWithdrawnAsync(manager, configured, logger, ct);
    }

    private static void Apply(OpenIddictApplicationDescriptor descriptor, AuthClientDefinition definition, string? secret)
    {
        descriptor.ClientId = definition.ClientId;
        descriptor.DisplayName = definition.DisplayName ?? definition.ClientId;

        // Set unconditionally, never left as populated: PopulateAsync hands back the *hashed*
        // secret, and feeding that straight into UpdateAsync hashes it a second time, which locks
        // the client out with no error anywhere.
        descriptor.ClientType = definition.Public ? ClientTypes.Public : ClientTypes.Confidential;
        descriptor.ClientSecret = definition.Public ? null : secret;

        // Implicit is the whole meaning of `firstParty` - the consent screen is skipped.
        descriptor.ConsentType = definition.FirstParty ? ConsentTypes.Implicit : ConsentTypes.Explicit;

        descriptor.RedirectUris.Clear();
        foreach (var uri in definition.RedirectUris) descriptor.RedirectUris.Add(new Uri(uri, UriKind.Absolute));

        descriptor.PostLogoutRedirectUris.Clear();
        foreach (var uri in definition.PostLogoutRedirectUris)
        {
            descriptor.PostLogoutRedirectUris.Add(new Uri(uri, UriKind.Absolute));
        }

        descriptor.Permissions.Clear();
        descriptor.Permissions.UnionWith([
            Permissions.Endpoints.Authorization,
            Permissions.Endpoints.Token,
            Permissions.Endpoints.EndSession,
            Permissions.Endpoints.Revocation,
            Permissions.GrantTypes.AuthorizationCode,
            Permissions.ResponseTypes.Code,
        ]);

        foreach (var scope in definition.Scopes)
        {
            // openid and offline_access are protocol scopes: OpenIddict exempts them from scope
            // validation, and offline_access is expressed as the refresh-token grant permission
            // rather than as scp:offline_access.
            if (scope == Scopes.OpenId) continue;

            if (scope == Scopes.OfflineAccess)
            {
                descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
                continue;
            }

            descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        }

        // Redundant while the server requires PKCE globally, and recorded anyway so the row itself
        // says so - a future per-client relaxation should have to be a deliberate edit here.
        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);

        descriptor.Properties[ManagedProperty] = JsonSerializer.SerializeToElement(true);

        if (Uri.TryCreate(definition.LogoUri, UriKind.Absolute, out var logo) && logo.Scheme == Uri.UriSchemeHttps)
        {
            descriptor.Properties[LogoProperty] = JsonSerializer.SerializeToElement(logo.AbsoluteUri);
        }
        else
        {
            descriptor.Properties.Remove(LogoProperty);
        }
    }

    private static async Task DisableWithdrawnAsync(
        IOpenIddictApplicationManager manager, HashSet<string> configured, ILogger logger, CancellationToken ct)
    {
        var withdrawn = new List<object>();

        await foreach (var application in manager.ListAsync(cancellationToken: ct))
        {
            var clientId = await manager.GetClientIdAsync(application, ct);
            if (clientId is null || configured.Contains(clientId)) continue;

            var properties = await manager.GetPropertiesAsync(application, ct);
            if (!properties.TryGetValue(ManagedProperty, out var managed) || !managed.GetBoolean()) continue;

            withdrawn.Add(application);
        }

        foreach (var application in withdrawn)
        {
            var descriptor = new OpenIddictApplicationDescriptor();
            await manager.PopulateAsync(descriptor, application, ct);

            // Disabled, not deleted: the row keeps its id, so the OpenIddictAuthorization rows that
            // record what people consented to still resolve.
            descriptor.Permissions.Clear();
            descriptor.RedirectUris.Clear();
            descriptor.PostLogoutRedirectUris.Clear();
            descriptor.ClientSecret = null;
            descriptor.ClientType = ClientTypes.Public;

            await manager.UpdateAsync(application, descriptor, ct);

            logger.LogWarning("SSO client {ClientId} is no longer in AUTH_CLIENTS and was disabled. "
                              + "Its consent history is kept; re-adding it to the config restores it.",
                await manager.GetClientIdAsync(application, ct));
        }
    }

    /// <summary>The client's logo, if the registry stored one. Null is the normal case.</summary>
    public static async ValueTask<string?> GetLogoUriAsync(
        IOpenIddictApplicationManager manager, object application, CancellationToken ct = default)
    {
        var properties = await manager.GetPropertiesAsync(application, ct);

        return properties.TryGetValue(LogoProperty, out var logo) && logo.ValueKind == JsonValueKind.String
            ? logo.GetString()
            : null;
    }
}
