namespace Echo.Sites;

/// <summary>Cross-origin access for the OIDC protocol endpoints, and only for them.</summary>
public static class SsoCors
{
    public const string PolicyName = "SsoPolicy";

    public static IServiceCollection AddSsoCors(this IServiceCollection services) =>
        services.AddCors(options => options.AddPolicy(PolicyName, policy => policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            // No PUT or DELETE: nothing in the OIDC surface uses them, and OPTIONS is the preflight
            // itself.
            .WithMethods("GET", "POST", "HEAD", "OPTIONS")));
}
