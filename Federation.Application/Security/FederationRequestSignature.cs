using System.Text;
using AppEnvironment;
using Federation.Domain.Aggregates;
using NSec.Cryptography;

namespace Federation.Application.Security;

/// <summary>
/// Proof-of-possession for federation GET requests, which carry no signed body of their own the
/// way event delivery does.
///
/// The caller signs "host|resource|timestamp" with its federation private key; the receiver
/// verifies against the <see cref="FederationInstance.PublicKey"/> already registered for that
/// host. This replaces trusting a bare X-Federated-Host header, which the caller writes itself and
/// which therefore authenticated nobody.
/// </summary>
public static class FederationRequestSignature
{
    public const string HostHeader = "X-Federated-Host";
    public const string TimestampHeader = "X-Federated-Timestamp";
    public const string SignatureHeader = "X-Federated-Signature";

    /// <summary>Bounds replay of a captured signature. Also caps how far a peer's clock may drift.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    private static byte[] Canonical(string host, string resource, long timestamp) =>
        Encoding.UTF8.GetBytes($"{host.TrimEnd('/')}|{resource}|{timestamp}");

    /// <summary>Produces the three headers a caller must send. Used by the (future) automatic
    /// backfill client and by tests.</summary>
    public static (string Host, string Timestamp, string Signature) CreateHeaders(
        string resource, DateTimeOffset? now = null)
    {
        var host = Env.GeneralConfiguration.InstanceUrl;
        var timestamp = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();

        var algorithm = SignatureAlgorithm.Ed25519;
        var key = Key.Import(algorithm, Env.Federation.PrivateKey, KeyBlobFormat.PkixPrivateKeyText);
        var signature = algorithm.Sign(key, Canonical(host, resource, timestamp));

        return (host, timestamp.ToString(), Convert.ToBase64String(signature));
    }

    /// <summary>
    /// Verifies a signed request. Fails closed on any malformed input.
    /// </summary>
    public static bool Verify(
        FederationInstance caller,
        string resource,
        string? timestampHeader,
        string? signatureHeader,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(timestampHeader) || string.IsNullOrWhiteSpace(signatureHeader))
            return false;

        if (!long.TryParse(timestampHeader, out var unixSeconds))
            return false;

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var reference = now ?? DateTimeOffset.UtcNow;
        if ((reference - issuedAt).Duration() > MaxClockSkew)
            return false;

        try
        {
            var signature = Convert.FromBase64String(signatureHeader);
            var algorithm = SignatureAlgorithm.Ed25519;
            var publicKey = PublicKey.Import(algorithm, caller.PublicKey, KeyBlobFormat.PkixPublicKeyText);

            return algorithm.Verify(publicKey, Canonical(caller.Host, resource, unixSeconds), signature);
        }
        catch
        {
            return false;
        }
    }
}
