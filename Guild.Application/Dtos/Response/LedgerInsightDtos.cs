using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Response;

/// <summary>
/// One member's sealed payment details, with the content key for the calling device if there is
/// one.
/// </summary>
public class SealedPaymentHandlesDto
{
    public required string UserId { get; set; }

    public required byte[] Ciphertext { get; set; }
    public required byte[] Nonce { get; set; }
    public required int Version { get; set; }

    /// <summary>The guild roster as it stood when this was sealed.</summary>
    public required int MemberRosterVersion { get; set; }

    public required DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// The content key sealed to the calling device, or null when this member has not shared with
    /// that device.
    /// </summary>
    public byte[]? WrappedKey { get; set; }
}

/// <summary>Every member's sealed payment details, as visible to one device.</summary>
public class PaymentHandleDirectoryDto
{
    public required string GuildId { get; set; }

    /// <summary>The device these wraps were selected for - echoed back so a client that sent the
    /// wrong <c>X-Device-Id</c> can tell, rather than concluding nobody has shared with it.</summary>
    public required string DeviceId { get; set; }

    /// <summary>The guild's roster right now.</summary>
    public required int MemberRosterVersion { get; set; }

    public List<SealedPaymentHandlesDto> Members { get; set; } = [];

    /// <summary>The phone numbers of the members who chose to show theirs to this guild.</summary>
    public List<SharedPhoneNumberDto> PhoneNumbers { get; set; } = [];

    /// <summary>The caller's own opt-in, echoed so the settings toggle can render without a second
    /// round trip. Only ever the caller's own: whether anybody else has opted in is visible from
    /// <see cref="PhoneNumbers"/>, and not otherwise.</summary>
    public required bool SharingPhoneNumber { get; set; }
}

/// <summary>One member's phone number, as they typed it.</summary>
public class SharedPhoneNumberDto
{
    public required string UserId { get; set; }

    /// <summary>E.164, normalised by Identity on write.</summary>
    public required string PhoneNumber { get; set; }

    /// <summary>When the owner last wrote it.</summary>
    public required DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One device a client may need to seal to, and the key to seal to it with.</summary>
public class PaymentHandleRecipientDto
{
    public required string UserId { get; set; }
    public required string DeviceId { get; set; }
    public string? DeviceName { get; set; }

    /// <summary>The device's long-term identity/signature public key, from Identity's device
    /// directory. Not an MLS KeyPackage init key: those are single-use, and a wrap re-read on every
    /// render needs a key that survives being looked at.</summary>
    public required byte[] PublicKey { get; set; }

    /// <summary>Whether Identity considers this device's certificate valid.</summary>
    public required bool HasValidCertificate { get; set; }

    /// <summary>Set when the device's current certificate has been revoked - it was removed, or the
    /// certificate was reissued and this is the superseded one. Reported rather than filtered: a
    /// client that is handed a shorter list than the roster actually has cannot tell a small
    /// household from a tampered response.</summary>
    public DateTimeOffset? CertificateRevokedAt { get; set; }

    /// <summary>False for a device its owner has marked removed.</summary>
    public required bool IsActive { get; set; }

    /// <summary>
    /// The device certificate itself, so a client can check <see cref="HasValidCertificate"/>
    /// rather than believe it.
    /// </summary>
    public byte[]? Certificate { get; set; }

    public DateTimeOffset? CertificateIssuedAt { get; set; }
    public DateTimeOffset? CertificateExpiresAt { get; set; }

    /// <summary>Which generation of the account identity key signed the certificate.</summary>
    public int? IdentityKeyVersion { get; set; }
}

public class PaymentHandleRecipientsDto
{
    public required string GuildId { get; set; }

    /// <summary>Seal with this, and store it as the blob's roster version, so a later reader can
    /// tell how stale the wrap set is.</summary>
    public required int MemberRosterVersion { get; set; }

    public List<PaymentHandleRecipientDto> Recipients { get; set; } = [];

    /// <summary>Members whose devices Identity declined to answer for because the roster was over
    /// its batch cap. Non-empty means this list is incomplete and sealing against it would leave
    /// those people out; empty is the only case in which the recipient list can be treated as the
    /// whole house. Surfaced rather than swallowed for the same reason the flags above exist.</summary>
    public List<string> UnresolvedMemberIds { get; set; } = [];
}

/// <summary><see cref="ExpenseDto"/> plus its spending category.</summary>
public class CategorizedExpenseDto : ExpenseDto
{
    public ExpenseCategory Category { get; set; }
}

/// <summary>A receipt photo attached to an expense.</summary>
public class ExpenseReceiptDto
{
    public required string Id { get; set; }
    public required string ExpenseId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public required long SizeBytes { get; set; }
    public required string UploadedByUserId { get; set; }
    public required DateTimeOffset UploadedAt { get; set; }
    public string? Url { get; set; }
}

/// <summary>What the house spent over a window, and what of it was the caller's.</summary>
public class LedgerSummaryDto
{
    public required string ChannelId { get; set; }
    public required string Currency { get; set; }

    public required DateTimeOffset From { get; set; }
    public required DateTimeOffset To { get; set; }

    public required long TotalMinor { get; set; }

    /// <summary>The caller's own share of everything in the window - their half of the shop, not
    /// what they happened to pay for. This is the number people actually want.</summary>
    public required long MyShareMinor { get; set; }

    public List<LedgerCategoryTotalDto> ByCategory { get; set; } = [];
    public List<LedgerPeriodTotalDto> ByPeriod { get; set; } = [];
    public List<LedgerPayerTotalDto> ByPayer { get; set; } = [];

    /// <summary>True when the requested window was longer than the cap and was shortened.</summary>
    public bool Clamped { get; set; }
}

public class LedgerCategoryTotalDto
{
    /// <summary><see cref="ExpenseCategory.Uncategorized"/> is reported as its own bucket rather
    /// than folded into Other. A rollup that hides the size of its own gap is worse than no
    /// rollup - "we do not know what a third of this was" is the useful part.</summary>
    public required ExpenseCategory Category { get; set; }

    public required long TotalMinor { get; set; }
    public required long MyShareMinor { get; set; }
    public required int Count { get; set; }
}

public class LedgerPeriodTotalDto
{
    /// <summary>"2026-07".</summary>
    public required string Period { get; set; }

    public required long TotalMinor { get; set; }
    public required long MyShareMinor { get; set; }
    public required int Count { get; set; }
}

/// <summary>What each member fronted over the window.</summary>
public class LedgerPayerTotalDto
{
    public required string UserId { get; set; }
    public required long PaidMinor { get; set; }
}
