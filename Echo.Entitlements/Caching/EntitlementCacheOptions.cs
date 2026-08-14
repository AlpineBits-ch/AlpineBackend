namespace Echo.Entitlements.Caching;

/// <summary>
/// The three windows the cache runs on, and the reason there are three rather than one.
/// </summary>
public sealed class EntitlementCacheOptions
{
    public const string SectionName = "Entitlements:Cache";

    /// <summary>How long a resolved set is served without re-resolving.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a set is kept as the last known answer after it stops being fresh. Long,
    /// because its only job is to be there during an outage, and a set that has expired out of Redis
    /// is indistinguishable from a subject nobody has ever asked about.</summary>
    public TimeSpan Retain { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How long a stale answer is served before the unreachable source is tried again. See
    /// the type comment; this is the difference between a billing outage and a billing outage that
    /// takes every request path down with it.</summary>
    public TimeSpan OutageGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Namespace for the Redis keys.</summary>
    public string KeyPrefix { get; set; } = "entitlements";

    /// <summary>
    /// <see cref="Ttl"/> in whole seconds, for the client-facing <c>ttlSeconds</c>.
    /// </summary>
    public int ClientTtlSeconds => Math.Max(1, (int)Math.Floor(Ttl.TotalSeconds));

    /// <summary>
    /// Checks the three windows against each other at registration, where a mistake is a startup
    /// failure naming the field, rather than at the first cache read where it is a subtly wrong
    /// answer nobody attributes to configuration.
    /// </summary>
    public EntitlementCacheOptions Validate()
    {
        if (Ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Ttl), Ttl,
                "The entitlement cache TTL is the backstop that makes a dropped invalidation self-heal. "
                + "Zero or less would mean no cache at all; to run without one, do not call "
                + "AddEntitlementCache.");
        }

        if (Retain < Ttl)
        {
            throw new ArgumentOutOfRangeException(nameof(Retain), Retain,
                $"Entitlements are retained for {Retain} but stay fresh for {Ttl}, so a set would be "
                + "evicted before it stopped being served. Retain is the last-known-good window that "
                + "makes a billing outage fail open, and it has to outlast freshness.");
        }

        if (OutageGrace <= TimeSpan.Zero || OutageGrace > Ttl)
        {
            throw new ArgumentOutOfRangeException(nameof(OutageGrace), OutageGrace,
                $"The outage grace has to be a positive window no longer than the {Ttl} TTL. Zero would "
                + "re-attempt an unreachable Billing on every request; longer than the TTL would keep "
                + "serving a stale set after a healthy resolve would have replaced it.");
        }

        if (string.IsNullOrWhiteSpace(KeyPrefix))
        {
            throw new ArgumentException("The cache key prefix cannot be blank.", nameof(KeyPrefix));
        }

        return this;
    }
}
