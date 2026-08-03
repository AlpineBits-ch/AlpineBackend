namespace Echo.Docs;

/// <summary>
/// How one service's declared routes map to the public URLs a client must actually call.
///
/// <para><b>This is the part that is easy to get wrong.</b> A service declares its routes as it sees
/// them - Guild's inbox endpoint declares <c>/api/v1/inbox/unread</c> - and YARP rewrites
/// <c>/api/v1/guild/{**catch-all}</c> to <c>/api/v1/{**catch-all}</c> on the way in. So the public
/// URL is <c>/api/v1/guild/inbox/unread</c>: the service segment <em>replaces</em> nothing and is
/// <em>inserted</em> after <c>/api/v1</c>. Naively prefixing the declared path would publish
/// <c>/api/v1/guild/api/v1/inbox/unread</c>, which 404s.</para>
///
/// <para><b>And not every route is rewritten.</b> Federation's protocol paths, the Discord-compat
/// surface, webhook execution, <c>/connect</c> and the <c>/.well-known</c> documents are all routed
/// without a transform, because for those the public path <em>is</em> the contract. Those are listed
/// per service in <see cref="PassThrough"/> and published exactly as declared.</para>
///
/// <para>Mirrors ProxyConfig.GetRoutes(). If a route is added there, it belongs here too.</para>
/// </summary>
public sealed record DocsService(
    string Name,
    string DisplayName,
    string Cluster,
    string? RewriteAs,
    IReadOnlyList<string> PassThrough)
{
    /// <summary>The project Docs.Generator analysed, used to match the response overlay.</summary>
    public string Project => $"{DisplayName}.Application";

    /// <summary>Where the service exposes its own document. Not reachable from outside: the gateway
    /// only forwards the routes in ProxyConfig, and none of them lead to <c>/internal</c>.</summary>
    public string DocumentPath { get; init; } = "/internal/openapi/v1.json";

    private const string ApiRoot = "/api/v1";

    /// <summary>
    /// The public URL for a declared path, or <c>null</c> if the gateway does not expose it - in
    /// which case it must be left out of the docs rather than published as an address that 404s.
    /// </summary>
    public string? ToPublicPath(string declared)
    {
        // Several services declare routes without a leading slash ("api/v1/devices"); ASP.NET
        // normalises those, and so must we before any prefix comparison.
        var path = declared.StartsWith('/') ? declared : "/" + declared;

        foreach (var prefix in PassThrough)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return path;
        }

        if (RewriteAs is not null && path.StartsWith(ApiRoot + "/", StringComparison.OrdinalIgnoreCase))
            return RewriteAs + path[ApiRoot.Length..];

        return null;
    }
}

public static class DocsCatalog
{
    public static IReadOnlyList<DocsService> Services { get; } =
    [
        new("identity", "Identity", "identity-cluster", "/api/v1/identity",
            // The OIDC surface and the discovery documents keep their own paths.
            ["/connect", "/.well-known/openid-configuration", "/.well-known/jwks"]),

        new("social", "Social", "social-cluster", "/api/v1/social", []),

        new("isle", "Isle", "isle-cluster", "/api/v1/isle", []),

        new("messaging", "Messaging", "messaging-cluster", "/api/v1/messaging", []),

        // Webhook execution keeps Discord's path shape so an existing integration works by changing
        // the host and nothing else.
        new("guild", "Guild", "guild-cluster", "/api/v1/guild", ["/api/webhooks"]),

        // Same reasoning for the Discord-compatible bot API.
        new("bots", "Bots", "bots-cluster", "/api/v1/bots", ["/api/discord"]),

        new("imports", "Import", "imports-cluster", "/api/v1/imports", []),

        // Federation has no rewriting route at all: /api/v1/federation/** is a protocol path that
        // remote instances are told to POST to, so the gateway forwards it untouched.
        new("federation", "Federation", "federation-cluster", null,
            ["/api/v1/federation", "/api/v1/admin/federation", "/.well-known/federation"]),
    ];
}
