namespace Identity.Domain.ValueObjects;

/// <summary>
/// One wrapping of the account master key: the 32-byte key sealed under a credential-derived key.
/// The server stores ciphertext and KDF parameters, and never derives anything - the credential and
/// the unwrapped master key both stay on the client.
///
/// <para><b>The master key is wrapped twice, and this type is one of those wrappings.</b> See
/// <see cref="Aggregates.ApplicationUser.EncryptedMasterKey"/> (password) and
/// <see cref="Aggregates.ApplicationUser.RecoveryCodeWrappedMasterKey"/> (recovery code). Two
/// independent wrappings of the same key is what makes a password reset survivable: the reset
/// invalidates the password wrapping only, and the client re-derives the master key from the
/// recovery code and re-wraps it. With a single password-derived wrapping, a reset destroys every
/// backup blob and the account identity key with it - silently, at the exact moment the user is
/// trying to recover their account.</para>
///
/// <para><see cref="Version"/> is what every encrypted device backup is bound to, and both wrappings
/// of one master key carry the same number - they wrap the same bytes. Rotating does not re-encrypt
/// the blobs sealed under the previous version, so bumping it orphans them; the rotation endpoint
/// reports which devices are affected and refuses to proceed without an explicit acknowledgement
/// rather than quietly making them unopenable.</para>
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

    /// <summary>KDF identifier, e.g. <c>argon2id</c>. Recorded rather than assumed so a future
    /// change of KDF does not silently reinterpret the parameters above.</summary>
    public string? Kdf { get; init; }

    /// <summary>
    /// A value derived client-side from the <b>master key</b> - not from the credential that wraps
    /// it - so that the server can compare wrappings without ever holding the key.
    ///
    /// <para>Two jobs. It lets a client check a passphrase without downloading and trial-decrypting
    /// every backup blob; and, because both wrappings of one key derive the same value, it is the
    /// only thing the server can check when asked to <i>replace</i> a wrapping. Without it,
    /// <c>rewrap-password</c> is a write of arbitrary bytes over the key every backup on the account
    /// is sealed under, and the account then reports itself healthy.</para>
    ///
    /// <para>Null only on envelopes written before it was required. Those accounts get the credential
    /// check and nothing else until a write backfills one - see <c>BackupController</c>.</para>
    /// </summary>
    public byte[]? PublicVerifier { get; init; }

    /// <summary>Returns a copy carrying <paramref name="verifier"/>. Exists because this is an
    /// init-only owned value: the verifier is established by a later write than the one that created
    /// the wrapping (backfill and trust-on-first-use both do this), and re-seating the whole value is
    /// the only way to say so without making every field mutable.</summary>
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