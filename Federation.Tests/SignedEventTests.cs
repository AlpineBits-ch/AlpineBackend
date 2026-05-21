using System.Text.Json;
using Federation.Application.Dtos.Events;
using Federation.Application.Dtos.Events.Bidirectional.Messaging;
using Federation.Domain.Aggregates;
using NSec.Cryptography;

namespace Federation.Tests;

[TestFixture]
public class SignedFederationEventTests
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    // Helpers ---------------------------------------------------------------

    private static (byte[] privateKeyBytes, byte[] publicKeyBytes) GenerateKeyPair()
    {
        using var key = Key.Create(Algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });

        var privateKeyBytes = key.Export(KeyBlobFormat.RawPrivateKey);
        var publicKeyBytes  = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        return (privateKeyBytes, publicKeyBytes);
    }

    private static FederationInstance BuildInstance(byte[] publicKeyBytes) =>
        new() { PublicKey = publicKeyBytes, Host = "https://venta.gg", Name = "Venta.gg" };

    /// <summary>
    /// Uses <see cref="MessageCreated"/> as the concrete payload so STJ's
    /// [JsonPolymorphic] resolver finds a registered derived type.
    /// </summary>
    private static MessageCreated BuildPayload(string host = "https://test.example.com") =>
        new() { Host = host };

    private static SignedFederationEvent ManuallySign(FederationEvent payload, byte[] privateKeyBytes)
    {
        using var key    = Key.Import(Algorithm, privateKeyBytes, KeyBlobFormat.RawPrivateKey);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, EventJsonContext.Default.FederationEvent);
        var signature    = Algorithm.Sign(key, payloadBytes);

        return new SignedFederationEvent
        {
            Payload   = payload,
            Signature = signature,
        };
    }

    // IsValid ---------------------------------------------------------------

    [Test]
    public void IsValid_WithMatchingKey_ReturnsTrue()
    {
        var (privateKey, publicKey) = GenerateKeyPair();
        var signed   = ManuallySign(BuildPayload(), privateKey);
        var instance = BuildInstance(publicKey);

        Assert.That(signed.IsValid(instance), Is.True);
    }

    [Test]
    public void IsValid_WithDifferentKeyPair_ReturnsFalse()
    {
        var (privateKey, _)     = GenerateKeyPair();
        var (_, wrongPublicKey) = GenerateKeyPair();
        var signed   = ManuallySign(BuildPayload(), privateKey);
        var instance = BuildInstance(wrongPublicKey);

        Assert.That(signed.IsValid(instance), Is.False);
    }

    [Test]
    public void IsValid_WithTamperedSignature_ReturnsFalse()
    {
        var (privateKey, publicKey) = GenerateKeyPair();
        var signed = ManuallySign(BuildPayload(), privateKey);

        var tampered = (byte[])signed.Signature.Clone();
        tampered[0] ^= 0xFF;

        var tamperedEvent = new SignedFederationEvent
        {
            Payload   = signed.Payload,
            Signature = tampered,
        };

        Assert.That(tamperedEvent.IsValid(BuildInstance(publicKey)), Is.False);
    }

    [Test]
    public void IsValid_WithTamperedPayload_ReturnsFalse()
    {
        var (privateKey, publicKey) = GenerateKeyPair();
        var signed = ManuallySign(BuildPayload("https://original.example.com"), privateKey);

        var tamperedEvent = new SignedFederationEvent
        {
            Payload   = BuildPayload("https://attacker.example.com"),
            Signature = signed.Signature,
        };

        Assert.That(tamperedEvent.IsValid(BuildInstance(publicKey)), Is.False);
    }

    [Test]
    public void IsValid_WithEmptySignature_ThrowsOrReturnsFalse()
    {
        var (_, publicKey) = GenerateKeyPair();
        var signed = new SignedFederationEvent
        {
            Payload   = BuildPayload(),
            Signature = [],
        };

        bool result;
        try
        {
            result = signed.IsValid(BuildInstance(publicKey));
        }
        catch (Exception)
        {
            Assert.Pass("NSec threw on malformed signature length – acceptable.");
            return;
        }

        Assert.That(result, Is.False);
    }

    // Signature determinism -------------------------------------------------

    [Test]
    public void Signature_IsDeterministic_ForSameKeyAndPayload()
    {
        var (privateKey, _) = GenerateKeyPair();
        var payload = BuildPayload();

        var sig1 = ManuallySign(payload, privateKey).Signature;
        var sig2 = ManuallySign(payload, privateKey).Signature;

        Assert.That(sig1, Is.EqualTo(sig2),
            "Ed25519 is deterministic – identical inputs must yield identical signatures.");
    }

    [Test]
    public void Signature_DiffersBetweenDistinctPayloads()
    {
        var (privateKey, _) = GenerateKeyPair();

        var sig1 = ManuallySign(BuildPayload("https://a.example.com"), privateKey).Signature;
        var sig2 = ManuallySign(BuildPayload("https://b.example.com"), privateKey).Signature;

        Assert.That(sig1, Is.Not.EqualTo(sig2));
    }

    [Test]
    public void Signature_HasExpectedEd25519Length()
    {
        var (privateKey, _) = GenerateKeyPair();
        var signed = ManuallySign(BuildPayload(), privateKey);

        Assert.That(signed.Signature.Length, Is.EqualTo(64));
    }

    // Round-trip serialisation ----------------------------------------------

    [Test]
    public void IsValid_AfterJsonRoundTrip_ReturnsTrue()
    {
        var (privateKey, publicKey) = GenerateKeyPair();
        var signed   = ManuallySign(BuildPayload(), privateKey);
        var instance = BuildInstance(publicKey);

        var json         = JsonSerializer.Serialize(signed);
        var deserialized = JsonSerializer.Deserialize<SignedFederationEvent>(json)!;

        Assert.That(deserialized.IsValid(instance), Is.True);
    }

    // Payload integrity -----------------------------------------------------

    [Test]
    public void Payload_HostIsPreservedAfterSigning()
    {
        var (privateKey, _) = GenerateKeyPair();
        const string host   = "https://preserved.example.com";
        var signed          = ManuallySign(BuildPayload(host), privateKey);

        Assert.That(signed.Payload.Host, Is.EqualTo(host));
    }
}
