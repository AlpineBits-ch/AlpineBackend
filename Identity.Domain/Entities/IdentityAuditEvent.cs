using Persistence;

namespace Identity.Domain.Entities;

public static class IdentityAuditActions
{
    public const string BackupRead = "backup.read";
    public const string BackupWritten = "backup.written";
    public const string BackupDeleted = "backup.deleted";
    public const string RecoveryKeyWritten = "recovery-key.written";
    public const string BackupTransferCreated = "backup.transfer.created";
    public const string BackupTransferClaimed = "backup.transfer.claimed";
    public const string KeyPackagesReset = "key-packages.reset";
    public const string DeviceIdentityRotated = "device.identity-rotated";

    /// <summary>A device was unregistered. The row survives the device, which is the point - the
    /// cascade takes its backup blob and its key packages with it, so "when did that disappear" has
    /// to be answerable afterwards.</summary>
    public const string DeviceRemoved = "device.removed";

    /// <summary>Append-only and surfaced in the UI on purpose: a silent downgrade from
    /// VerifiedDevices is the step that makes auto-admitting an attacker's device possible.</summary>
    public const string ProtectionLevelChanged = "protection-level.changed";

    /// <summary>Invalidates every peer's pinning and every device certificate under the old key.</summary>
    public const string AccountIdentityKeyRotated = "account-identity-key.rotated";

    /// <summary>A password reset left the master key's password wrapping undecryptable. Recorded
    /// even when the recovery-code wrapping saves it, because the window in between is when an
    /// account with no recovery code loses its encrypted history for good.</summary>
    public const string MasterKeyPasswordWrappingInvalidated = "master-key.password-wrapping-invalidated";

    public const string MasterKeyRewrapped = "master-key.rewrapped";

    /// <summary>Existing sessions were revoked wholesale, currently only by a password reset. Worth
    /// recording because the reset is what a compromised user reaches for, and "did my other
    /// sessions actually get cut" is the question they need answered afterwards.</summary>
    public const string SessionsRevoked = "sessions.revoked";

    /// <summary>A session was bound to a device it did not log in from, which is only possible with
    /// the account password. Audited because it is the one operation that changes <i>which device a
    /// session is</i>, and therefore which device's backup it may read - the exact decision C2 was
    /// about. If a session is ever found reading the wrong device's blob, this row is where it
    /// started.</summary>
    public const string SessionDeviceBound = "session.device-bound";

    /// <summary>The account's privacy record was written. Audited because these are the controls a
    /// user is told they own: "who turned my DMs back on, and when" has to be answerable, and a
    /// stolen session quietly widening contactability leaves no other trace. The <c>Detail</c> names
    /// the changed fields and never their values.</summary>
    public const string PrivacySettingsChanged = "privacy-settings.changed";

    /// <summary>The account accepted a version of a legal document (T1-10). The consent row is the
    /// evidence; this row is what makes "when did they agree, and did anything else happen at the
    /// same time" answerable against the same timeline as every other security event.</summary>
    public const string ConsentRecorded = "consent.recorded";

    /// <summary>A data-subject request was opened by staff (T1-13). The subject of a DSR is often
    /// not the account this row hangs off - see <c>AdminDsrController</c> - so the <c>Detail</c>
    /// names the request id and type, and the <c>UserId</c> is the <b>acting staff member</b>.
    /// Attribution to a person is the entire reason a staff-facing queue needs an audit trail.</summary>
    public const string DsrOpened = "dsr.opened";

    /// <summary>A data-subject request was progressed - assigned, re-statused, annotated.</summary>
    public const string DsrUpdated = "dsr.updated";

    /// <summary>A data-subject request was closed with a disposition.</summary>
    public const string DsrClosed = "dsr.closed";

    /// <summary>The account asked for a copy of its own data (T1-7). Recorded at request time, not
    /// only at download time, so the audit trail shows the whole shape of the event: a stolen session
    /// that requests an export and then waits for it to assemble is visible from the moment it asks,
    /// rather than only once the archive is already leaving.</summary>
    public const string DataExportRequested = "data-export.requested";

    /// <summary>An export archive was downloaded.
    ///
    /// <para>This is the precedent set by <see cref="BackupRead"/>, applied to the one artifact that
    /// deserves it even more. An export is the single densest bundle of personal data in the system -
    /// account, profile, memberships, conversations, every message the person sent - in one file, and
    /// a session that downloads it leaves no other trace: nothing is written, nothing is deleted, and
    /// the legitimate user has no way to notice. Exfiltration that cannot be prevented can at least be
    /// made visible.</para>
    ///
    /// <para><c>Detail</c> names the export id, never the artifact key - the key is a bearer
    /// credential for the archive, and copying it into an append-only table that is deliberately never
    /// edited would leave it there long after the artifact's seven days.</para></summary>
    public const string DataExportDownloaded = "data-export.downloaded";
}

public class CreateIdentityAuditEventParams
{
    public string UserId { get; init; } = null!;
    public string Action { get; init; } = null!;
    public string? ClientDeviceId { get; init; }
    public string? Detail { get; init; }
    public string? IpAddress { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// An append-only record of the security-sensitive things that happened to an account.
///
/// <para>It exists mainly for the backup blobs. A stolen session that quietly downloads every
/// device's encrypted backup leaves no other trace - the blobs are unchanged, nothing is deleted,
/// and the legitimate user has no way to notice. So every read writes a row here and pushes a
/// notice to the account's other devices: exfiltration that cannot be prevented can at least be
/// made visible.</para>
///
/// <para>Never updated and never deleted except with the account itself. A row that can be edited
/// by the same session that wrote it is not an audit trail.</para>
/// </summary>
public class IdentityAuditEvent : BaseEntity<IdentityAuditEvent>, IPrefixedEntity
{
    public static string Prefix { get; } = "idau";

    public string UserId { get; set; } = null!;

    /// <summary>One of <see cref="IdentityAuditActions"/>.</summary>
    public string Action { get; set; } = null!;

    /// <summary>Client device id the action was performed from, when one was validated.</summary>
    public string? ClientDeviceId { get; set; }

    /// <summary>Short human-readable context, e.g. which device's blob was read. Never the blob.</summary>
    public string? Detail { get; set; }

    public string? IpAddress { get; set; }

    public static IdentityAuditEvent Create(CreateIdentityAuditEventParams parameters)
    {
        var date = parameters.CreatedAt == default ? DateTimeOffset.UtcNow : parameters.CreatedAt;
        return new IdentityAuditEvent
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            UserId = parameters.UserId,
            Action = parameters.Action,
            ClientDeviceId = parameters.ClientDeviceId,
            Detail = parameters.Detail,
            IpAddress = parameters.IpAddress,
        };
    }
}
