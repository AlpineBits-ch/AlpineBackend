using System.Net.Http.Json;
using System.Text;
using AppEnvironment;
using Federation.Application.Dtos.Requests;
using Federation.Application.Dtos.Response;
using NSec.Cryptography;

namespace Federation.Application.Services;

public class FederationHandshakeService(IHttpClientFactory httpClientFactory)
{
    private const string ProtocolVersion = "venta/v0.1";

    public async Task<HandshakeResponse> InitiateHandshakeAsync(string targetHost, CancellationToken ct = default)
    {
        var request = new HandshakeRequest(
            Host: Env.GeneralConfiguration.InstanceUrl,
            Name: Env.Federation.InstanceName,
            ProtocolVersion: ProtocolVersion,
            PublicKey: Env.Federation.PublicKey,
            Signature: Sign(Env.GeneralConfiguration.InstanceUrl, ProtocolVersion)
        );

        var client = httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"{targetHost.TrimEnd('/')}/.well-known/federation/handshake",
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
