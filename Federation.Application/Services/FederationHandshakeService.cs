using System.Net.Http.Json;
using System.Text;
using AppEnvironment;
using Federation.Application.Dtos.Requests;
using Federation.Application.Dtos.Response;
using Federation.Application.Security;
using NSec.Cryptography;

namespace Federation.Application.Services;

public class FederationHandshakeService(IHttpClientFactory httpClientFactory, IHostEnvironment environment)
{
    private const string ProtocolVersion = "venta/v0.1";

    /// <summary>
    /// Thrown when the caller-supplied target is rejected by <see cref="FederationTargetGuard"/>.
    /// Surfaced as a 400 rather than a 500 so a bad target reads as a client error.
    /// </summary>
    public class InvalidTargetException(string message) : Exception(message);

    public async Task<HandshakeResponse> InitiateHandshakeAsync(string targetHost, CancellationToken ct = default)
    {
        // targetHost comes straight from the request body, so it is validated before use: scheme
        // restricted to https (outside Production), and non-routable addresses refused. The
        // connection itself is IP-checked again in the named client's ConnectCallback, which is
        // what defeats DNS rebinding.
        var allowPrivateTargets = !environment.IsProduction();
        if (!FederationTargetGuard.TryNormalizeTarget(targetHost, allowPrivateTargets, out var target, out var error))
            throw new InvalidTargetException(error!);

        var request = new HandshakeRequest(
            Host: Env.GeneralConfiguration.InstanceUrl,
            Name: Env.Federation.InstanceName,
            ProtocolVersion: ProtocolVersion,
            PublicKey: Env.Federation.PublicKey,
            Signature: Sign(Env.GeneralConfiguration.InstanceUrl, ProtocolVersion)
        );

        var client = httpClientFactory.CreateClient(FederationHttpClients.Handshake);
        var response = await client.PostAsJsonAsync(
            new Uri(target!, "/.well-known/federation/handshake"),
            request, ct);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<HandshakeResponse>(ct)
               ?? throw new InvalidOperationException("Empty handshake response from remote.");
    }

    public static bool VerifySignature(HandshakeRequest request)
    {
        try
        {
            var algorithm = SignatureAlgorithm.Ed25519;
            var publicKey = NSec.Cryptography.PublicKey.Import(
                algorithm, request.PublicKey, KeyBlobFormat.PkixPublicKeyText);
            var message = Encoding.UTF8.GetBytes($"{request.Host}|{request.ProtocolVersion}");
            return algorithm.Verify(publicKey, message, request.Signature);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Sign(string host, string protocolVersion)
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        var key = Key.Import(algorithm, Env.Federation.PrivateKey, KeyBlobFormat.PkixPrivateKeyText);
        var message = Encoding.UTF8.GetBytes($"{host}|{protocolVersion}");
        return algorithm.Sign(key, message);
    }
}
