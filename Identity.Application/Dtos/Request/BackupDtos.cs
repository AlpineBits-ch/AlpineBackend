namespace Identity.Application.Dtos.Request;

/// <summary>
/// The recovery-key envelope: a symmetric key wrapped under a passphrase-derived key.
/// </summary>
public class PutRecoveryKeyDto
{
    /// <summary>Monotonic.</summary>
    public int Version { get; set; }

    public string Kdf { get; set; } = "argon2id";

    public int Iterations { get; set; }
    public int MemoryKiB { get; set; }
    public int Parallelism { get; set; }

    public byte[] Salt { get; set; } = null!;
    public byte[] Iv { get; set; } = null!;
    public byte[] CipherText { get; set; } = null!;

    /// <summary>
    /// A value derived from the master key - never from the password - that the server can compare
    /// without ever holding the key itself.
    /// </summary>
    public byte[]? PublicVerifier { get; set; }

    /// <summary>Account password.</summary>
    public string Password { get; set; } = null!;

    /// <summary>
    /// The same master key wrapped a second time, under a key derived from the recovery code.
    /// </summary>
    public MasterKeyWrappingDto? RecoveryCodeWrapping { get; set; }
}

/// <summary>One wrapping of the master key.</summary>
public class MasterKeyWrappingDto
{
    public string Kdf { get; set; } = "argon2id";
    public int Iterations { get; set; }
    public int MemoryKiB { get; set; }
    public int Parallelism { get; set; }
    public byte[] Salt { get; set; } = null!;
    public byte[] Iv { get; set; } = null!;
    public byte[] CipherText { get; set; } = null!;

    /// <summary>
    /// Derived from the master key, so both wrappings of one key must carry the same value.
    /// </summary>
    public byte[]? PublicVerifier { get; set; }
}

/// <summary>
/// Re-wraps the master key under a new password after a reset invalidated the old wrapping.
/// </summary>
public class RewrapMasterKeyDto
{
    /// <summary>Must equal the stored version - this re-wraps the existing key, it does not rotate
    /// it. A mismatch means the client is holding a master key the account has moved on from.</summary>
    public int Version { get; set; }

    /// <summary>The new wrapping.</summary>
    public MasterKeyWrappingDto PasswordWrapping { get; set; } = null!;

    /// <summary>The account password - the in-app path, where the user changed their password while
    /// still able to sign in. Alternative to <see cref="RewrapTicket"/>.</summary>
    public string? Password { get; set; }

    /// <summary>The single-use ticket returned by <c>POST api/v1/user/reset-password</c>.</summary>
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

    /// <summary>The second wrapping, under the recovery code.</summary>
    public MasterKeyWrappingDto? RecoveryCodeWrapping { get; set; }

    /// <summary>Set when a password reset made the password wrapping above undecryptable.</summary>
    public DateTimeOffset? PasswordWrappingInvalidatedAt { get; set; }

    /// <summary>
    /// False when no credential the user could still hold will open the master key: the password
    /// wrapping was invalidated by a reset and there is no recovery-code wrapping.
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

    /// <summary>Whether the account now has a <c>publicVerifier</c> on file.</summary>
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

/// <summary>A transfer waiting for the calling device.</summary>
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
