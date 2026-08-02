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

    /// <summary>
    /// A value derived client-side from the master key - not from the credential that wraps it - so
    /// that the server can compare wrappings without ever holding the key.
    /// </summary>
    public byte[]? PublicVerifier { get; init; }

    /// <summary>Returns a copy carrying <paramref name="verifier"/>.</summary>
    public EncryptedMasterKey WithPublicVerifier(byte[]? verifier) => new()
    {
        CipherText = CipherText,
        Salt = Salt,
        Iv = Iv,
        Argon2Iterations = Argon2Iterations,
        Argon2Memory = Argon2Memory,
        Argon2Parallelism = Argon2Parallelism,
        Version = Version,
        Kdf = Kdf,
        PublicVerifier = verifier,
    };
}