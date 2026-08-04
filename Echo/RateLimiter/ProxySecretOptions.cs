using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace Echo.RateLimiter;

/// <summary>
/// The shared secret a reverse proxy presents to prove that the <c>X-Forwarded-For</c> chain it
/// wrote may be believed.
/// </summary>
public sealed class ProxySecretOptions
{
    /// <summary>
    /// Environment variable holding the secret, e.g. <c>GATEWAY_PROXY_SECRET="9f3c...".</c> Sits in
    /// the same <c>GATEWAY_*</c> family as <see cref="TrustedProxyOptions.EnvironmentVariable"/>.
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

    /// <summary>True when the variable was present but held only whitespace.</summary>
    public bool WasSetButBlank { get; }

    public static ProxySecretOptions FromEnvironment(string? raw = null)
    {
        raw ??= Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (raw is null) return new ProxySecretOptions(null, false);

        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return new ProxySecretOptions(null, wasSetButBlank: true);

        return new ProxySecretOptions(Digest(trimmed), false);
    }

    /// <summary>Whether the value a request presented is the configured secret.</summary>
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
        // an attacker can walk one character at a time.
        return CryptographicOperations.FixedTimeEquals(_digest, Digest(trimmed));
    }

    private static byte[] Digest(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
