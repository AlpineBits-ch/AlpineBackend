using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace Echo.RateLimiter;

/// <summary>
/// The shared secret a reverse proxy presents to prove that the <c>X-Forwarded-For</c> chain it
/// wrote may be believed. This is the <b>recommended</b> way to configure the gateway's client
/// identification; <see cref="TrustedProxyOptions"/> remains available as an alternative for
/// deployments that genuinely do have stable proxy addresses.
///
/// <para><b>Why a secret rather than an address allowlist.</b> The allowlist mechanism only works
/// when the immediate peer's address is known ahead of time and stays put. In this deployment it
/// does neither: container addresses are reassigned on every restart and cloud load balancers
/// rotate their egress addresses without notice. An allowlist that has drifted does not fail
/// loudly - it silently stops matching, forwarded headers stop being believed, and every anonymous
/// caller collapses back into one shared bucket keyed on the proxy. A secret is bound to the
/// configuration rather than to the network topology, so restarting a container or replacing a
/// load balancer cannot invalidate it.</para>
///
/// <para><b>The header never travels further than this gateway.</b>
/// <see cref="GatewayRateLimiting.UseEchoRateLimiter"/> evaluates it once and removes it from the
/// request, and <see cref="ProxySecretStrippingTransformProvider"/> removes it again on the way out
/// to every proxied cluster. A gateway secret that reaches the eight backend services is a gateway
/// secret that any one of them can read, log, or leak in an error report.</para>
/// </summary>
public sealed class ProxySecretOptions
{
    /// <summary>
    /// Environment variable holding the secret, e.g.
    /// <c>GATEWAY_PROXY_SECRET="9f3c...".</c> Sits in the same <c>GATEWAY_*</c> family as
    /// <see cref="TrustedProxyOptions.EnvironmentVariable"/>.
    ///
    /// <para>There is deliberately <b>no default value</b>. A shipped default would be published in
    /// this repository, which makes it exactly as good as no secret at all while looking configured.
    /// Unset means "ignore forwarded headers", which is safe, coarse, and warned about at
    /// startup.</para>
    /// </summary>
    public const string EnvironmentVariable = "GATEWAY_PROXY_SECRET";

    /// <summary>The header the reverse proxy sets alongside <c>X-Forwarded-For</c>.</summary>
    public const string HeaderName = "X-Echo-Proxy-Auth";

    /// <summary>
    /// Where <see cref="GatewayRateLimiting.UseEchoRateLimiter"/> records the outcome of the secret
    /// check, so the value survives the header being stripped off the request.
    /// </summary>
    public const string ContextItemKey = "Echo.RateLimiter.ForwardedChainTrusted";

    /// <summary>SHA-256 of the configured secret, or null when nothing usable is configured.</summary>
    private readonly byte[]? _digest;

    private ProxySecretOptions(byte[]? digest, bool wasSetButBlank)
    {
        _digest = digest;
        WasSetButBlank = wasSetButBlank;
    }

    /// <summary>An unconfigured instance: no secret, nothing is ever trusted by header.</summary>
    public ProxySecretOptions() : this(null, false)
    {
    }

    public bool IsConfigured => _digest is not null;

    /// <summary>
    /// True when the variable was present but held only whitespace. Treated as unset - a blank
    /// secret would otherwise compare equal to a blank header and trust everything - but reported
    /// separately at startup, because "I set it and it did nothing" needs a different message from
    /// "I never set it". A trailing newline in a hand-edited <c>.env</c> is the likeliest way this
    /// happens, which is also why the configured value and the presented value are both trimmed.
    /// </summary>
    public bool WasSetButBlank { get; }

    public static ProxySecretOptions FromEnvironment(string? raw = null)
    {
        raw ??= Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (raw is null) return new ProxySecretOptions(null, false);

        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return new ProxySecretOptions(null, wasSetButBlank: true);

        return new ProxySecretOptions(Digest(trimmed), false);
    }

    /// <summary>
    /// Whether the value a request presented is the configured secret.
    ///
    /// <para>Exactly one header value is accepted. A client that sends its own guess when the proxy
    /// also sets the header produces two values; refusing to consider that case means the worst a
    /// client can do by adding the header is force itself onto the coarse peer-address bucket, and
    /// never onto a bucket of its own choosing. (The generated proxy configuration uses a *set*
    /// rather than an *append*, so in practice the client's copy is overwritten before it gets
    /// here.)</para>
    /// </summary>
    public bool Matches(StringValues presented)
    {
        if (_digest is null || presented.Count != 1) return false;
        return Matches(presented[0]);
    }

    public bool Matches(string? presented)
    {
        if (_digest is null || presented is null) return false;

        var trimmed = presented.Trim();
        if (trimmed.Length == 0) return false;

        // Compared as fixed-length digests rather than as strings: a byte-by-byte comparison of the
        // raw values returns early on the first mismatch, which turns response time into an oracle
        // an attacker can walk one character at a time. Hashing first also keeps the comparison
        // constant-time with respect to the *length* of the secret, which FixedTimeEquals on the raw
        // bytes would still leak.
        return CryptographicOperations.FixedTimeEquals(_digest, Digest(trimmed));
    }

    private static byte[] Digest(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
