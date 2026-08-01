namespace Identity.Domain.ValueObjects;

/// <summary>
/// One wrapping of the account master key: the 32-byte key sealed under a credential-derived key.
/// </summary>
public class EncryptedMasterKey
{
    public byte[] CipherText { get; init; } = null!;
    public byte[] Salt { get; init; } = null!;
    public byte[] Iv { get; init; } = null!;
    public int Argon2Iterations { get; init; }
    public int Argon2Memory { get; init; }
    public int Argon2Parallelism { get; init; }
    public int Version { get; init; } = 1;

    /// <summary>KDF identifier, e.g. <c>argon2id</c>.</summary>
    public string? Kdf { get; init; }

    /// <summary>Lets a client check a passphrase without downloading and trial-decrypting every
    /// backup blob. Optional; a client that omits it simply gets no early failure.</summary>
    public byte[]? PublicVerifier { get; init; }

}