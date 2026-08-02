namespace Identity.Application.Dtos.Request;

/// <summary>
/// The recovery-key envelope: a symmetric key wrapped under a passphrase-derived key. Every field
/// is opaque to the server, which stores it and hands it back - it never derives the passphrase key
/// and never sees the unwrapped key.
/// </summary>
public class PutRecoveryKeyDto
{
    /// <summary>Monotonic. Bumping it orphans every device backup sealed under the previous
    /// version, which is why the endpoint refuses without <c>?acknowledgeOrphans=true</c>.</summary>
    public int Version { get; set; }

    public string Kdf { get; set; } = "argon2id";

    public int Iterations { get; set; }
    public int MemoryKiB { get; set; }
    public int Parallelism { get; set; }

    public byte[] Salt { get; set; } = null!;
    public byte[] Iv { get; set; } = null!;
    public byte[] CipherText { get; set; } = null!;

    /// <summary>
    /// A value derived from the <b>master key</b> - never from the password - that the server can
    /// compare without ever holding the key itself.
    ///
    /// <para><b>Required when this write establishes key material</b>, i.e. the first envelope and
    /// every rotation. That is the only moment it can be obtained: the server cannot compute it, so a
    /// rotation that omits it leaves the account permanently unable to have a re-wrap checked, which
    /// is the state §C.1 described as protected and was not. Optional on a same-version write, where
    /// it is either backfilled onto an envelope that has none or compared against the stored one.</para>
    ///
    /// <para>Second use: a client can reject a wrong passphrase against this without downloading and
    /// trial-decrypting a 16 MiB blob.</para>
    /// </summary>
    public byte[]? PublicVerifier { get; set; }

    /// <summary>Account password. Writing this envelope is what makes every backup readable, so it
    /// is gated on proving the account rather than merely holding a token for it.</summary>
    public string Password { get; set; } = null!;

    /// <summary>
    /// The <b>same master key</b> wrapped a second time, under a key derived from the recovery code.
    ///
    /// <para>Optional on the wire so a client can be updated in two steps, but omitting it leaves the
    /// account one password reset away from losing every backup blob and its account identity key -
    /// the reset invalidates the password wrapping and there is nothing else that opens the key. An
    /// account without it cannot enter <c>VerifiedDevices</c> and cannot put engine state in the
    /// cloud.</para>
    /// </summary>
    public MasterKeyWrappingDto? RecoveryCodeWrapping { get; set; }
}

/// <summary>One wrapping of the master key. Same shape as the flat fields on
/// <see cref="PutRecoveryKeyDto"/>, minus the version - both wrappings of one key share it.</summary>
public class MasterKeyWrappingDto
{
    public string Kdf { get; set; } = "argon2id";
    public int Iterations { get; set; }
    public int MemoryKiB { get; set; }
    public int Parallelism { get; set; }
    public byte[] Salt { get; set; } = null!;
    public byte[] Iv { get; set; } = null!;
    public byte[] CipherText { get; set; } = null!;

    /// <summary>Derived from the master key, so both wrappings of one key must carry the same value.
    /// A write that submits two different verifiers is refused: it means two different keys were
    /// wrapped and called one, and the recovery code would open something no backup is sealed
    /// under.</summary>
    public byte[]? PublicVerifier { get; set; }
}

/// <summary>
/// Re-wraps the master key under a new password after a reset invalidated the old wrapping.
///
/// <para><b>This overwrites the wrapping that makes every backup blob on the account readable.</b>
/// It used to take nothing but a session token and then clear the "your password wrapping is
/// broken" stamp on the way out, so one request destroyed the account's entire encrypted history and
/// reported it as healthy. Exactly one of <see cref="Password"/> or <see cref="RewrapTicket"/> has
/// to be right now, and <see cref="MasterKeyWrappingDto.PublicVerifier"/> has to match the value the
/// account already holds - so a caller who cannot open the master key cannot produce a write that
/// silently orphans every blob.</para>
/// </summary>
public class RewrapMasterKeyDto
{
    /// <summary>Must equal the stored version - this re-wraps the existing key, it does not rotate
    /// it. A mismatch means the client is holding a master key the account has moved on from.</summary>
    public int Version { get; set; }

    /// <summary>The new wrapping. Its <c>publicVerifier</c> is always required, and is compared
    /// against the account's stored one <i>when the account has one</i> - a wrapping that seals
    /// different bytes cannot reproduce it. Accounts that predate the verifier have nothing to compare
    /// against; theirs is stored on first use and the response reports
    /// <c>verifierChecked: false</c>.</summary>
    public MasterKeyWrappingDto PasswordWrapping { get; set; } = null!;

    /// <summary>The account password - the in-app path, where the user changed their password while
    /// still able to sign in. Alternative to <see cref="RewrapTicket"/>.</summary>
    public string? Password { get; set; }

    /// <summary>The single-use ticket returned by <c>POST api/v1/user/reset-password</c>. The
    /// recovery path, where the password was just reset and the client is unwrapping from the
    /// recovery code. Alternative to <see cref="Password"/>.</summary>
    public string? RewrapTicket { get; set; }
}

public class RecoveryKeyDto
{
    public int Version { get; set; }
    public string Kdf { get; set; } = null!;
    public int Iterations { get; set; }
    public int MemoryKiB { get; set; }
    public int Parallelism { get; set; }
    public byte[] Salt { get; set; } = null!;
    public byte[] Iv { get; set; } = null!;
    public byte[] CipherText { get; set; } = null!;
    public byte[]? PublicVerifier { get; set; }

    /// <summary>The second wrapping, under the recovery code. Null when the account never set one
    /// up - which is the state that makes a password reset destructive.</summary>
    public MasterKeyWrappingDto? RecoveryCodeWrapping { get; set; }

    /// <summary>Set when a password reset made the password wrapping above undecryptable. The client
    /// must unlock from the recovery code and re-wrap; until it does, the password opens nothing.</summary>
    public DateTimeOffset? PasswordWrappingInvalidatedAt { get; set; }

    /// <summary>
    /// False when no credential the user could still hold will open the master key: the password
    /// wrapping was invalidated by a reset and there is no recovery-code wrapping.
    ///
    /// <para>Surface this as "your encrypted history is no longer recoverable". It is not a warning
    /// about the future - it has already happened, and every backup blob on the account is now
    /// permanently unopenable.</para>
    /// </summary>
    public bool EncryptedHistoryRecoverable { get; set; }
}

public class PutRecoveryKeyResultDto
{
    public int Version { get; set; }

    /// <summary>Client device ids whose stored backup was sealed under a previous recovery-key
    /// version and is therefore no longer openable. Populated on the refusal as well as on the
    /// acknowledged write, so the client can show exactly what it is about to lose.</summary>
    public List<string> OrphanedBlobDeviceIds { get; set; } = new();

    /// <summary>Whether the account now has a <c>publicVerifier</c> on file. False means a future
    /// <c>rewrap-password</c> has nothing to compare against and will be gated by the credential
    /// alone - the client can fix that by re-posting this route at the same version with a verifier,
    /// which backfills it without rotating anything.</summary>
    public bool HasPublicVerifier { get; set; }
}

/// <summary>Metadata for one device's stored backup. Never carries the blob.</summary>
public class BackupMetaDto
{
    public string BlobId { get; set; } = null!;

    /// <summary>Client device id, matching the route parameter.</summary>
    public string DeviceId { get; set; } = null!;

    public string DeviceName { get; set; } = null!;
    public int Version { get; set; }
    public int RecoveryKeyVersion { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string ETag { get; set; } = null!;

    /// <summary>True when <see cref="RecoveryKeyVersion"/> is behind the account's current envelope,
    /// so this blob can no longer be opened. Reported rather than hidden: a restore that discovers
    /// this by failing to decrypt cannot tell it apart from a wrong passphrase or a corrupt
    /// file.</summary>
    public bool IsStale { get; set; }
}

public class PutBackupResultDto
{
    public string BlobId { get; set; } = null!;
    public int Version { get; set; }
    public string ETag { get; set; } = null!;
    public long SizeBytes { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class CreateBackupTransferDto
{
    /// <summary>Client device id of the device allowed to claim this.</summary>
    public string TargetDeviceId { get; set; } = null!;

    /// <summary>The target's public key the payload was wrapped to.</summary>
    public byte[] WrappedTo { get; set; } = null!;

    public byte[] CipherText { get; set; } = null!;

    /// <summary>Clamped to <see cref="Identity.Domain.Entities.UserBackupTransfer.MaxLifetime"/>.
    /// Omit for the default.</summary>
    public int? ExpiresInSeconds { get; set; }
}

public class BackupTransferCreatedDto
{
    public string TransferId { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>A transfer waiting for the calling device. Deliberately without the ciphertext: listing
/// what is pending must not be a way to read it, or the single-use claim guarantees nothing.</summary>
public class PendingBackupTransferDto
{
    public string TransferId { get; set; } = null!;
    public string SourceDeviceId { get; set; } = null!;
    public byte[] WrappedTo { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public class ClaimedBackupTransferDto
{
    public string TransferId { get; set; } = null!;
    public string SourceDeviceId { get; set; } = null!;
    public byte[] WrappedTo { get; set; } = null!;
    public byte[] CipherText { get; set; } = null!;
}
