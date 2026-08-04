namespace Echo.RateLimiter;

/// <summary>
/// Everything the gateway limiter is configured with: how a caller is identified, and how much
/// traffic each kind of caller may make.
///
/// <para><b>Shape.</b> A token bucket, matching Discord's global limit: a sustained rate measured
/// per second, plus a reserve that lets a client spend faster than that for a moment. The fixed
/// window this replaces (100 requests per minute, no queue) was unusable for a real client - 100/min
/// is 1.67 req/s spread across every route on the gateway, and a cold application start fans out to
/// the guild list, then channels, roles, members and emoji per guild, then messages, pins and read
/// state, which exceeds it for a user in ten guilds before they have typed anything. A fixed window
/// also has the boundary artifact where 200 requests either side of a window edge are allowed while
/// 101 inside one window are not. A token bucket has neither property: the reserve absorbs the
/// start-up fan-out, and the refill is continuous so there is no edge to straddle.</para>
/// </summary>
public sealed class GatewayRateLimitOptions
{
    /// <summary>
    /// Sustained rate for a signed-in caller, per <see cref="ReplenishmentPeriod"/>. Fifty per
    /// second is Discord's documented global limit, and a client written against Discord's shape
    /// therefore already backs off correctly against this one.
    /// </summary>
    public const int DefaultAuthenticatedTokensPerPeriod = 50;

    /// <summary>
    /// Reserve a signed-in caller may spend in one go. Twice the sustained rate: two seconds' worth
    /// of budget, which covers the cold-start fan-out described above without letting a client hold
    /// an unbounded amount of credit from a long idle period.
    /// </summary>
    public const int DefaultAuthenticatedBurstCapacity = 100;

    /// <summary>
    /// Sustained rate for an anonymous caller, keyed on address.
    ///
    /// <para><b>Deliberately lower than the authenticated rate.</b> An anonymous caller is signing
    /// in, registering, refreshing a token or reading a public document - none of which fan out the
    /// way a logged-in client's first paint does, so it does not need the same budget. It is also
    /// the cheapest partition to multiply (one bucket per source address, and addresses are cheap),
    /// which argues for the tighter of the two. Twenty per second is still twelve times the
    /// effective rate of the 100/min window it replaces, so no realistic anonymous client is worse
    /// off than it was.</para>
    /// </summary>
    public const int DefaultAnonymousTokensPerPeriod = 20;

    /// <summary>Reserve for an anonymous caller - the same 2x ratio as the authenticated bucket.</summary>
    public const int DefaultAnonymousBurstCapacity = 40;

    public static readonly TimeSpan DefaultReplenishmentPeriod = TimeSpan.FromSeconds(1);

    /// <summary>Address ranges whose forwarded headers are believed. The alternative mechanism -
    /// see <see cref="ProxySecretOptions"/> for why the secret is the recommended one.</summary>
    public TrustedProxyOptions TrustedProxies { get; init; } = new();

    /// <summary>The shared secret mechanism. Recommended over <see cref="TrustedProxies"/>.</summary>
    public ProxySecretOptions ProxySecret { get; init; } = new();

    public int AuthenticatedTokensPerPeriod { get; init; } = DefaultAuthenticatedTokensPerPeriod;

    public int AuthenticatedBurstCapacity { get; init; } = DefaultAuthenticatedBurstCapacity;

    public int AnonymousTokensPerPeriod { get; init; } = DefaultAnonymousTokensPerPeriod;

    public int AnonymousBurstCapacity { get; init; } = DefaultAnonymousBurstCapacity;

    /// <summary>
    /// How often the buckets refill. One second in production. Overridable so a test can exercise
    /// the real limiter, through the real pipeline, without its assertions depending on how fast the
    /// test host happened to run - see <c>Echo.Tests.RateLimiting</c>.
    /// </summary>
    public TimeSpan ReplenishmentPeriod { get; init; } = DefaultReplenishmentPeriod;

    /// <summary>
    /// Webhook execution shares the authenticated shape. A webhook is an integration, not a browsing
    /// human, and it is already partitioned per webhook id, so it gets the same budget a signed-in
    /// caller does rather than the tighter anonymous one.
    /// </summary>
    public int WebhookTokensPerPeriod => AuthenticatedTokensPerPeriod;

    public int WebhookBurstCapacity => AuthenticatedBurstCapacity;

    public static GatewayRateLimitOptions FromEnvironment() => new()
    {
        TrustedProxies = TrustedProxyOptions.FromEnvironment(),
        ProxySecret = ProxySecretOptions.FromEnvironment()
    };
}
