using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppEnvironment;
using Federation.Application.Dtos.Events.Bidirectional.Conversation;
using Federation.Application.Dtos.Events.Bidirectional.Guild;
using Federation.Application.Dtos.Events.Bidirectional.Messaging;
using Federation.Application.Dtos.Events.Bidirectional.Social;
using Federation.Domain.Aggregates;
using NSec.Cryptography;

namespace Federation.Application.Dtos.Events;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$eventType")]
// Messaging
[JsonDerivedType(typeof(MessageCreated), "messageCreated")]
[JsonDerivedType(typeof(MessageEdited), "messageEdited")]
[JsonDerivedType(typeof(MessageDeleted), "messageDeleted")]
[JsonDerivedType(typeof(MessageReactionAdded), "messageReactionAdded")]
[JsonDerivedType(typeof(MessageReactionRemoved), "messageReactionRemoved")]
// Guild (bidirectional)
[JsonDerivedType(typeof(GuildMemberJoined), "guildMemberJoined")]
[JsonDerivedType(typeof(GuildMemberLeft), "guildMemberLeft")]
[JsonDerivedType(typeof(GuildMemberBanned), "guildMemberBanned")]
// Guild (inbound)
[JsonDerivedType(typeof(GuildInviteRedeemed), "guildInviteRedeemed")]
[JsonDerivedType(typeof(GuildJoinRequest), "guildJoinRequest")]
// Guild (outbound)
[JsonDerivedType(typeof(GuildInviteAccepted), "guildInviteAccepted")]
[JsonDerivedType(typeof(GuildInviteRevoked), "guildInviteRevoked")]
// Social
[JsonDerivedType(typeof(SocialFriendRequest), "socialFriendRequest")]
[JsonDerivedType(typeof(SocialFriendAccepted), "socialFriendAccepted")]
[JsonDerivedType(typeof(SocialFriendRejected), "socialFriendRejected")]
[JsonDerivedType(typeof(SocialFriendRemoved), "socialFriendRemoved")]
// Conversation
[JsonDerivedType(typeof(ConversationCreated), "conversationCreated")]
[JsonDerivedType(typeof(ConversationEdited), "conversationEdited")]
[JsonDerivedType(typeof(ConversationDeleted), "conversationDeleted")]
[JsonDerivedType(typeof(ConversationMemberAdded), "conversationMemberAdded")]
[JsonDerivedType(typeof(ConversationMemberLeft), "conversationMemberLeft")]
public class FederationEvent
{
    public string Host { get; set; } = string.Empty;
    public string ProtocolVersion { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string[] PreviousEventIds { get; set; } = [];
    public long Depth { get; set; }
    public string ChannelId { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public DateTime OriginServerTime { get; set; }
}


[JsonSerializable(typeof(ConversationCreated))]
[JsonSerializable(typeof(ConversationDeleted))]
[JsonSerializable(typeof(ConversationEdited))]
[JsonSerializable(typeof(ConversationMemberAdded))]
[JsonSerializable(typeof(ConversationMemberLeft))]

[JsonSerializable(typeof(SocialFriendAccepted))]
[JsonSerializable(typeof(SocialFriendRejected))]
[JsonSerializable(typeof(SocialFriendRemoved))]
[JsonSerializable(typeof(SocialFriendRequest))]

[JsonSerializable(typeof(MessageCreated))]
[JsonSerializable(typeof(MessageDeleted))]
[JsonSerializable(typeof(MessageEdited))]
[JsonSerializable(typeof(MessageReactionAdded))]
[JsonSerializable(typeof(MessageReactionRemoved))]

[JsonSerializable(typeof(GuildMemberJoined))]
[JsonSerializable(typeof(GuildMemberLeft))]
[JsonSerializable(typeof(GuildInviteRedeemed))]
[JsonSerializable(typeof(GuildJoinRequest))]

[JsonSerializable(typeof(GuildInviteAccepted))]
[JsonSerializable(typeof(GuildInviteRevoked))]
[JsonSerializable(typeof(GuildJoinRequest))]
[JsonSerializable(typeof(GuildMemberBanned))]


[JsonSerializable(typeof(FederationEvent))]
[JsonSerializable(typeof(List<FederationEvent>))]
public partial class EventJsonContext : JsonSerializerContext
{
}

/// <summary>JSON options for the federation wire format.</summary>
public static class FederationJson
{
    /// <summary>Options that produce the exact bytes which get signed and put on the wire.</summary>
    public static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.General);

    /// <summary>Options for reading an inbound payload, which never take part in verification.</summary>
    public static readonly JsonSerializerOptions Inbound = new(JsonSerializerDefaults.Web);
}

/// <summary>A federation event together with the Ed25519 signature over its wire bytes.</summary>
public sealed class SignedFederationEvent
{
    private const string PayloadProperty = "Payload";
    private const string SignatureProperty = "Signature";

    private SignedFederationEvent(FederationEvent payload, byte[] signature, ReadOnlyMemory<byte> signedPayload)
    {
        Payload = payload;
        Signature = signature;
        SignedPayload = signedPayload;
    }

    /// <summary>The event body, deserialized from the signed bytes.</summary>
    public FederationEvent Payload { get; }

    /// <summary>The Ed25519 signature over <see cref="SignedPayload"/>.</summary>
    public byte[] Signature { get; }

    /// <summary>The payload bytes the signature actually covers, as signed or as received.</summary>
    public ReadOnlyMemory<byte> SignedPayload { get; }

    /// <summary>Stamps the local host and protocol version onto the payload and signs its wire bytes.</summary>
    /// <param name="payload">The event to sign; its host and protocol version are overwritten.</param>
    /// <param name="protocolVersion">The protocol version to stamp, e.g. <c>venta/v0.1</c>.</param>
    /// <returns>The signed envelope, carrying the exact bytes that were signed.</returns>
    public static SignedFederationEvent Create(FederationEvent payload, string protocolVersion)
    {
        payload.Host = Env.GeneralConfiguration.InstanceUrl;
        payload.ProtocolVersion = protocolVersion;

        var algorithm = SignatureAlgorithm.Ed25519;
        using var key = Key.Import(algorithm, Env.Federation.PrivateKey, KeyBlobFormat.PkixPrivateKeyText);

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, FederationJson.Wire);
        var signature = algorithm.Sign(key, payloadBytes);

        return new SignedFederationEvent(payload, signature, payloadBytes);
    }

    /// <summary>Reads an envelope off the wire, keeping the payload's bytes exactly as they arrived.</summary>
    /// <param name="body">The raw UTF-8 request body.</param>
    /// <param name="signed">The parsed envelope, or null when the body is not a usable envelope.</param>
    /// <returns>True when the body yielded a payload and a signature.</returns>
    public static bool TryParse(ReadOnlyMemory<byte> body, out SignedFederationEvent? signed)
    {
        signed = null;

        try
        {
            var (payloadBytes, signature) = ReadEnvelope(body);
            if (signature is null || payloadBytes.IsEmpty) return false;

            var payload = JsonSerializer.Deserialize<FederationEvent>(payloadBytes.Span, FederationJson.Inbound);
            if (payload is null) return false;

            signed = new SignedFederationEvent(payload, signature, payloadBytes);
            return true;
        }
        catch (Exception e) when (e is JsonException or FormatException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Reads an envelope off the wire.</summary>
    /// <param name="body">The raw UTF-8 request body.</param>
    /// <returns>The parsed envelope.</returns>
    /// <exception cref="JsonException">The body is not a usable envelope.</exception>
    public static SignedFederationEvent Parse(ReadOnlyMemory<byte> body)
        => TryParse(body, out var signed)
            ? signed!
            : throw new JsonException("Body is not a signed federation event.");

    /// <summary>Serializes the envelope for transport, embedding the signed bytes verbatim.</summary>
    /// <returns>The UTF-8 request body to POST.</returns>
    public byte[] ToWireBytes()
    {
        var buffer = new ArrayBufferWriter<byte>(SignedPayload.Length + 128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName(PayloadProperty);
            writer.WriteRawValue(SignedPayload.Span, skipInputValidation: true);
            writer.WriteBase64String(SignatureProperty, Signature);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Verifies the signature against the sending instance's registered public key.</summary>
    /// <param name="instance">The instance the event claims to come from.</param>
    /// <returns>True when the signature covers the bytes this envelope was built from.</returns>
    public bool IsValid(FederationInstance instance)
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        var publicKey = PublicKey.Import(algorithm, instance.PublicKey, KeyBlobFormat.PkixPublicKeyText);

        return algorithm.Verify(publicKey, SignedPayload.Span, Signature);
    }

    // Slices the payload out of the body rather than re-reading it, so what gets verified is what
    // arrived: a re-serialization drops fields this version's type does not know about, and adds
    // nulls and enum defaults the sender never signed.
    private static (ReadOnlyMemory<byte> Payload, byte[]? Signature) ReadEnvelope(ReadOnlyMemory<byte> body)
    {
        var reader = new Utf8JsonReader(body.Span);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return (default, null);

        ReadOnlyMemory<byte> payload = default;
        byte[]? signature = null;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isPayload = reader.ValueTextEquals(PayloadProperty) || reader.ValueTextEquals("payload");
            var isSignature = reader.ValueTextEquals(SignatureProperty) || reader.ValueTextEquals("signature");

            if (!reader.Read()) return (default, null);

            if (isPayload)
            {
                var start = (int)reader.TokenStartIndex;
                reader.Skip();
                payload = body[start..(int)reader.BytesConsumed];
            }
            else if (isSignature)
            {
                if (reader.TokenType != JsonTokenType.String) return (default, null);
                signature = reader.GetBytesFromBase64();
            }
            else
            {
                reader.Skip();
            }
        }

        return (payload, signature);
    }
}