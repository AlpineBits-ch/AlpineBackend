using System.Text;
using System.Text.Json;
using AppEnvironment;
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

    /// <summary>
    /// The envelope shape the pre-fix implementation serialized and the shape an instance running
    /// that code still puts on the wire, so the compatibility tests below have something real to
    /// compare against.
    /// </summary>
    private sealed record LegacyEnvelope(FederationEvent Payload, byte[] Signature);

    private static (byte[] privateKeyBytes, byte[] publicKeyBytes) GenerateKeyPair()
    {
        using var key = Key.Create(Algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });

        var privateKeyBytes = key.Export(KeyBlobFormat.PkixPrivateKeyText);
        var publicKeyBytes  = key.PublicKey.Export(KeyBlobFormat.PkixPublicKeyText);
        return (privateKeyBytes, publicKeyBytes);
    }

    private static FederationInstance BuildInstance(byte[] publicKeyBytes) =>
        new() { PublicKey = publicKeyBytes, Host = "https://venta.gg", Name = "Venta.gg" };

    /// <summary>
    /// Uses <see cref="MessageCreated"/> as the concrete payload so STJ's
    /// [JsonPolymorphic] resolver finds a registered derived type.
    /// </summary>
    private static MessageCreated BuildPayload(string host = "https://test.example.com") =>
        new() { Host = host, EventId = "evt_1", MessageId = "msg_1" };

    private static byte[] PayloadJson(FederationEvent payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, FederationJson.Wire);

    private static byte[] Sign(byte[] payloadJson, byte[] privateKeyBytes)
    {
        using var key = Key.Import(Algorithm, privateKeyBytes, KeyBlobFormat.PkixPrivateKeyText);
        return Algorithm.Sign(key, payloadJson);
    }

    private static byte[] Envelope(byte[] payloadJson, byte[] signature,
        string payloadName = "Payload", string signatureName = "Signature")
        => Encoding.UTF8.GetBytes(
            $"{{\"{payloadName}\":{Encoding.UTF8.GetString(payloadJson)}," +
            $"\"{signatureName}\":\"{Convert.ToBase64String(signature)}\"}}");

    private static byte[] WithExtraField(FederationEvent payload, string name, string value)
    {
        var json = Encoding.UTF8.GetString(PayloadJson(payload));
        return Encoding.UTF8.GetBytes(json.Insert(json.Length - 1, $",\"{name}\":\"{value}\""));
    }

    private static byte[] SignedBody(FederationEvent payload, byte[] privateKeyBytes)
    {
        var payloadJson = PayloadJson(payload);
        return Envelope(payloadJson, Sign(payloadJson, privateKeyBytes));
    }

    // How an instance running the pre-fix code checks an inbound body: deserialize, re-serialize,
    // verify that. Kept here so "does the fix still interoperate with the old half" is testable.
    private static bool LegacyVerify(byte[] body, byte[] publicKeyBytes)
    {
        var envelope = JsonSerializer.Deserialize<LegacyEnvelope>(body, FederationJson.Inbound)!;
        var reserialized = JsonSerializer.SerializeToUtf8Bytes(envelope.Payload, FederationJson.Wire);
        var publicKey = PublicKey.Import(Algorithm, publicKeyBytes, KeyBlobFormat.PkixPublicKeyText);

        return Algorithm.Verify(publicKey, reserialized, envelope.Signature);
    }

    // Verification ----------------------------------------------------------

    [Test]
    public void IsValid_WithMatchingKey_ReturnsTrue()
    {
        var (privateKey, publicKey) = GenerateKeyPair();
        var signed = SignedFederationEvent.Parse(SignedBody(BuildPayload(), privateKey));

        Assert.That(signed.IsValid(BuildInstance(publicKey)), Is.True);
    }

    [Test]
    public void IsValid_WithDifferentKeyPair_ReturnsFalse()
    {
        var (privateKey, _)     = GenerateKeyPair();
        var (_, wrongPublicKey) = GenerateKeyPair();
        var signed = SignedFederationEvent.Parse(SignedBody(BuildPayload(), privateKey));

        Assert.That(signed.IsValid(BuildInstance(wrongPublicKey)), Is.False);
    }

    [Test]
    public void IsValid_WithTamperedSignature_ReturnsFalse()
    {
        var (privateKey, publicKey) = GenerateKeyPair();
        var payloadJson = PayloadJson(BuildPayload());
        var signature = Sign(payloadJson, privateKey);
        signature[0] ^= 0xFF;

        var signed = SignedFederationEvent.Parse(Envelope(payloadJson, signature));

        Assert.That(signed.IsValid(BuildInstance(publicKey)), Is.False);
    }

    [Test]
    public void IsValid_WithTamperedPayload_ReturnsFalse()
    {
        var (privateKey, publicKey) = GenerateKeyPair();
        var signature = Sign(PayloadJson(BuildPayload("https://original.example.com")), privateKey);

        var body = Envelope(PayloadJson(BuildPayload("https://attacker.example.com")), signature);
        var signed = SignedFederationEvent.Parse(body);

        Assert.That(signed.IsValid(BuildInstance(publicKey)), Is.False);
    }

    [Test]
    public void IsValid_WithReformattedPayload_ReturnsFalse()
    {
        var (privateKey, publicKey) = GenerateKeyPair();
        var payloadJson = PayloadJson(BuildPayload());
        var signature = Sign(payloadJson, privateKey);

        // The same object with one byte of insignificant whitespace: the signature covers bytes, so
        // a relay may not reformat a payload in flight.
        var reformatted = Encoding.UTF8.GetString(payloadJson).Insert(1, " ");
        var signed = SignedFederationEvent.Parse(Envelope(Encoding.UTF8.GetBytes(reformatted), signature));

        Assert.That(signed.IsValid(BuildInstance(publicKey)), Is.False);
    }

    [Test]
    public void IsValid_WithEmptySignature_ThrowsOrReturnsFalse()
    {
        var (_, publicKey) = GenerateKeyPair();
        var signed = SignedFederationEvent.Parse(Envelope(PayloadJson(BuildPayload()), []));

        bool result;
        try
        {
            result = signed.IsValid(BuildInstance(publicKey));
        }
        catch (Exception)
        {
            Assert.Pass("NSec threw on malformed signature length - acceptable.");
            return;
        }

        Assert.That(result, Is.False);
    }

    // Unknown fields --------------------------------------------------------

    [Test]
    public void IsValid_WhenPayloadCarriesAFieldThisVersionDoesNotKnow_ReturnsTrue()
    {
        // The regression: a sender one version ahead signs a field this build's type drops on
        // deserialization, so anything verified against a re-serialization fails here.
        var (privateKey, publicKey) = GenerateKeyPair();
        var payloadJson = WithExtraField(BuildPayload(), "authorDisplayNameFromTheFuture", "Kaelen the Grey");

        var signed = SignedFederationEvent.Parse(Envelope(payloadJson, Sign(payloadJson, privateKey)));

        Assert.That(signed.IsValid(BuildInstance(publicKey)), Is.True);
    }

    [Test]
    public void IsValid_WhenAnUnknownFieldIsTampered_ReturnsFalse()
    {
        // The other half of the same property: an unknown field is still covered by the signature,
        // so it cannot be rewritten in flight just because this build ignores it.
        var (privateKey, publicKey) = GenerateKeyPair();
        var signature = Sign(WithExtraField(BuildPayload(), "unknown", "signed"), privateKey);

        var body = Envelope(WithExtraField(BuildPayload(), "unknown", "swapped"), signature);
        var signed = SignedFederationEvent.Parse(body);

        Assert.That(signed.IsValid(BuildInstance(publicKey)), Is.False);
    }

    [Test]
    public void IsValid_WhenAnUnknownFieldIsInjected_ReturnsFalse()
    {
        var (privateKey, publicKey) = GenerateKeyPair();
        var signature = Sign(PayloadJson(BuildPayload()), privateKey);

        var body = Envelope(WithExtraField(BuildPayload(), "unknown", "injected"), signature);
        var signed = SignedFederationEvent.Parse(body);

        Assert.That(signed.IsValid(BuildInstance(publicKey)), Is.False);
    }

    // Signing ---------------------------------------------------------------

    [Test]
    public void Create_ThenWireBytes_RoundTripsThroughParse()
    {
        var (privateKey, publicKey) = GenerateKeyPair();
        Env.Federation.PrivateKey = privateKey;
        Env.GeneralConfiguration.InstanceUrl = "https://sender.example.com";

        var signed = SignedFederationEvent.Create(BuildPayload(), "venta/v0.1");
        var received = SignedFederationEvent.Parse(signed.ToWireBytes());

        Assert.Multiple(() =>
        {
            Assert.That(received.IsValid(BuildInstance(publicKey)), Is.True);
            Assert.That(received.Payload.Host, Is.EqualTo("https://sender.example.com"));
            Assert.That(received.Payload.ProtocolVersion, Is.EqualTo("venta/v0.1"));
            Assert.That(received.Payload, Is.InstanceOf<MessageCreated>());
        });
    }

    [Test]
    public void ToWireBytes_EmbedsTheSignedBytesVerbatim()
    {
        var (privateKey, _) = GenerateKeyPair();
        Env.Federation.PrivateKey = privateKey;
        Env.GeneralConfiguration.InstanceUrl = "https://sender.example.com";

        var signed = SignedFederationEvent.Create(BuildPayload(), "venta/v0.1");
        var received = SignedFederationEvent.Parse(signed.ToWireBytes());

        Assert.That(received.SignedPayload.ToArray(), Is.EqualTo(signed.SignedPayload.ToArray()));
    }

    [Test]
    public void Signature_IsDeterministic_ForSameKeyAndPayload()
    {
        var (privateKey, _) = GenerateKeyPair();
        var payloadJson = PayloadJson(BuildPayload());

        Assert.That(Sign(payloadJson, privateKey), Is.EqualTo(Sign(payloadJson, privateKey)),
            "Ed25519 is deterministic - identical inputs must yield identical signatures.");
    }

    [Test]
    public void Signature_DiffersBetweenDistinctPayloads()
    {
        var (privateKey, _) = GenerateKeyPair();

        var sig1 = Sign(PayloadJson(BuildPayload("https://a.example.com")), privateKey);
        var sig2 = Sign(PayloadJson(BuildPayload("https://b.example.com")), privateKey);

        Assert.That(sig1, Is.Not.EqualTo(sig2));
    }

    [Test]
    public void Signature_HasExpectedEd25519Length()
    {
        var (privateKey, _) = GenerateKeyPair();
        var signed = SignedFederationEvent.Parse(SignedBody(BuildPayload(), privateKey));

        Assert.That(signed.Signature, Has.Length.EqualTo(64));
    }

    // Cross-version interop -------------------------------------------------

    [Test]
    public void Parse_AcceptsABodyWrittenByThePreFixSender()
    {
        var (privateKey, publicKey) = GenerateKeyPair();
        var payload = BuildPayload();
        var legacy = new LegacyEnvelope(payload, Sign(PayloadJson(payload), privateKey));
        var body = JsonSerializer.SerializeToUtf8Bytes(legacy, FederationJson.Wire);

        var signed = SignedFederationEvent.Parse(body);

        Assert.That(signed.IsValid(BuildInstance(publicKey)), Is.True);
    }

    [Test]
    public void WireBytes_StillVerifyUnderThePreFixReceiversRule()
    {
        // The outbound direction of the transition: an instance still running the old verifier has
        // to keep accepting what this one sends, which it does as long as the payload only carries
        // fields it knows.
        var (privateKey, publicKey) = GenerateKeyPair();
        Env.Federation.PrivateKey = privateKey;
        Env.GeneralConfiguration.InstanceUrl = "https://sender.example.com";

        var body = SignedFederationEvent.Create(BuildPayload(), "venta/v0.1").ToWireBytes();

        Assert.That(LegacyVerify(body, publicKey), Is.True);
    }

    [Test]
    public void PreFixReceiver_CannotVerify_APayloadFieldItDoesNotKnow()
    {
        // The residual incompatibility, and why it cannot be closed from this side: the old
        // verifier re-serializes, which drops the field before it checks the signature.
        var (privateKey, publicKey) = GenerateKeyPair();
        var payloadJson = WithExtraField(BuildPayload(), "authorDisplayNameFromTheFuture", "Kaelen the Grey");
        var body = Envelope(payloadJson, Sign(payloadJson, privateKey));

        Assert.Multiple(() =>
        {
            Assert.That(LegacyVerify(body, publicKey), Is.False);
            Assert.That(SignedFederationEvent.Parse(body).IsValid(BuildInstance(publicKey)), Is.True);
        });
    }

    // Parsing ---------------------------------------------------------------

    [Test]
    public void Parse_AcceptsCamelCasedEnvelopeProperties()
    {
        var (privateKey, publicKey) = GenerateKeyPair();
        var payloadJson = PayloadJson(BuildPayload());
        var body = Envelope(payloadJson, Sign(payloadJson, privateKey), "payload", "signature");

        Assert.That(SignedFederationEvent.Parse(body).IsValid(BuildInstance(publicKey)), Is.True);
    }

    [Test]
    public void Parse_AcceptsSignatureBeforePayload()
    {
        var (privateKey, publicKey) = GenerateKeyPair();
        var payloadJson = PayloadJson(BuildPayload());
        var body = Encoding.UTF8.GetBytes(
            $"{{\"Signature\":\"{Convert.ToBase64String(Sign(payloadJson, privateKey))}\"," +
            $"\"Payload\":{Encoding.UTF8.GetString(payloadJson)},\"unknown\":[1,2]}}");

        Assert.That(SignedFederationEvent.Parse(body).IsValid(BuildInstance(publicKey)), Is.True);
    }

    [TestCase("")]
    [TestCase("not json")]
    [TestCase("[]")]
    [TestCase("{}")]
    [TestCase("{\"Payload\":null,\"Signature\":\"AAAA\"}")]
    [TestCase("{\"Payload\":{\"$eventType\":\"messageCreated\"}}")]
    [TestCase("{\"Payload\":{\"$eventType\":\"messageCreated\"},\"Signature\":42}")]
    [TestCase("{\"Payload\":{\"$eventType\":\"nope\"},\"Signature\":\"AAAA\"}")]
    public void TryParse_WithUnusableBody_ReturnsFalse(string body)
    {
        Assert.That(SignedFederationEvent.TryParse(Encoding.UTF8.GetBytes(body), out var signed), Is.False);
        Assert.That(signed, Is.Null);
    }

    [Test]
    public void Payload_HostIsPreservedAfterSigning()
    {
        var (privateKey, _) = GenerateKeyPair();
        Env.Federation.PrivateKey = privateKey;
        const string host = "https://preserved.example.com";
        Env.GeneralConfiguration.InstanceUrl = host;

        var signed = SignedFederationEvent.Create(BuildPayload(), "venta/v0.1");

        Assert.That(signed.Payload.Host, Is.EqualTo(host));
    }
}
