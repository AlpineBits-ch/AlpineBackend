namespace Echo.RateLimiter;

/// <summary>
/// Everything the gateway limiter is configured with: how a caller is identified, and how much
/// traffic each kind of caller may make.
/// </summary>
public sealed class GatewayRateLimitOptions
{
    /// <summary>
    /// Sustained rate for a signed-in caller, per <see cref="ReplenishmentPeriod"/>.
    /// </summary>
    public const int DefaultAuthenticatedTokensPerPeriod = 50;

    /// <summary>Reserve a signed-in caller may spend in one go.</summary>
    public const int DefaultAuthenticatedBurstCapacity = 100;

    /// <summary>Sustained rate for an anonymous caller, keyed on address.</summary>
    public const int DefaultAnonymousTokensPerPeriod = 20;

    /// <summary>Reserve for an anonymous caller - the same 2x ratio as the authenticated bucket.</summary>
    public const int DefaultAnonymousBurstCapacity = 40;

    public static readonly TimeSpan DefaultReplenishmentPeriod = TimeSpan.FromSeconds(1);

    /// <summary>Address ranges whose forwarded headers are believed.</summary>
    public TrustedProxyOptions TrustedProxies { get; init; } = new();

    /// <summary>The shared secret mechanism. Recommended over <see cref="TrustedProxies"/>.</summary>
    public ProxySecretOptions ProxySecret { get; init; } = new();

    public int AuthenticatedTokensPerPeriod { get; init; } = DefaultAuthenticatedTokensPerPeriod;

    public int AuthenticatedBurstCapacity { get; init; } = DefaultAuthenticatedBurstCapacity;

    public int AnonymousTokensPerPeriod { get; init; } = DefaultAnonymousTokensPerPeriod;

    public int AnonymousBurstCapacity { get; init; } = DefaultAnonymousBurstCapacity;

    /// <summary>How often the buckets refill.</summary>
    public TimeSpan ReplenishmentPeriod { get; init; } = DefaultReplenishmentPeriod;

    /// <summary>Webhook execution shares the authenticated shape.</summary>
    public int WebhookTokensPerPeriod => AuthenticatedTokensPerPeriod;

    public int WebhookBurstCapacity => AuthenticatedBurstCapacity;

    public static GatewayRateLimitOptions FromEnvironment() => new()
    {
        TrustedProxies = TrustedProxyOptions.FromEnvironment(),
        ProxySecret = ProxySecretOptions.FromEnvironment()
    };
}
