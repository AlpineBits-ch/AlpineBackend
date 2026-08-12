using AppEnvironment;

namespace Echo.Cors;

/// <summary>
/// Cross-origin access for the first-party clients - the desktop app, the browser web client, and a
/// developer's dev server.
/// </summary>
public static class AlpineCors
{
    public const string PolicyName = "AlpinePolicy";

    /// <summary>Response headers script is allowed to read.</summary>
    public static readonly string[] ExposedHeaders =
    [
        // The client's only server-time source, used to correct clock skew before rendering
        // "playing for 23 minutes".
        "Date",

        // The game catalog is served with an ETag the client is meant to send back as
        // If-None-Match.
        "ETag",

        // Read by rate-limit-interceptor.ts on a 429 and by the data-export page on a 429 from
        // "request an export".
        "Retry-After",
    ];

    /// <summary>How long a browser may reuse one preflight result.</summary>
    public static readonly TimeSpan PreflightMaxAge = TimeSpan.FromHours(2);

    public static IServiceCollection AddAlpineCors(this IServiceCollection services) =>
        services.AddCors(options => options.AddPolicy(PolicyName, policy => policy
            .WithOrigins([.. ClientOrigins.Allowed])
            .AllowAnyHeader()
            // PUT, PATCH and DELETE are all used by the client, so this is load-bearing rather than
            // convenience - and OPTIONS has to be reachable for the preflight itself.
            .AllowAnyMethod()
            .AllowCredentials()
            .WithExposedHeaders(ExposedHeaders)
            .SetPreflightMaxAge(PreflightMaxAge)));

    /// <summary>
    /// Prints the allowlist once at startup, and complains about anything <see
    /// cref="ClientOrigins"/> threw away.
    /// </summary>
    public static void LogAlpineCorsOrigins(this IApplicationBuilder app)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(AlpineCors).FullName!);

        logger.LogInformation("CORS allowlist ({Count}): {Origins}",
            ClientOrigins.Allowed.Count, string.Join(", ", ClientOrigins.Allowed));

        foreach (var rejected in ClientOrigins.Rejected)
        {
            logger.LogWarning(
                "{Variable} entry '{Entry}' is not a usable origin and was ignored. An origin is "
                + "scheme://host[:port] with no path, and '*' is refused outright because it would "
                + "combine with AllowCredentials into a credentialed grant to every site.",
                ClientOrigins.EnvironmentVariable, rejected);
        }
    }
}
