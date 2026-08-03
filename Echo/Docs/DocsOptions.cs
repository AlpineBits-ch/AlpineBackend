namespace Echo.Docs;

/// <summary>
/// How one service's declared routes map to the public URLs a client must actually call.
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

    /// <summary>Where the service exposes its own document.</summary>
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
