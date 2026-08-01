namespace Identity.Application.Dtos.Request;

public class AccountIdentityKeyDto
{
    public string UserId { get; set; } = null!;

    /// <summary>Ed25519 public half. Peers TOFU-pin this on first contact, exactly like a Signal
    /// safety number.</summary>
    public byte[]? PublicKey { get; set; }

    /// <summary>Monotonic, so a peer can distinguish a rotation from a rollback to a key it has
    /// already retired.</summary>
    public int Version { get; set; }

    /// <summary>The new key signed by the outgoing one, when the outgoing one still existed. Its
    /// absence is not an error - the lost-every-device case cannot produce one - but it means peers
    /// must re-verify out of band rather than accept the change automatically.</summary>
    public byte[]? RotationSignature { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}

public class PutAccountIdentityKeyDto
{
    public byte[] PublicKey { get; set; } = null!;

    /// <summary>Must exceed the stored version.</summary>
    public int Version { get; set; }

    /// <summary>Signature over the new key by the outgoing one, where the client still holds it.</summary>
    public byte[]? RotationSignature { get; set; }

    /// <summary>Account password. Required to rotate, not to publish for the first time - an
    /// existing account acquiring its first identity key has nothing to invalidate.</summary>
    public string? Password { get; set; }

    public string? DeviceId { get; set; }
}
