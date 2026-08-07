using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

/// <summary>
/// The caller's payment details, already sealed by their own device, plus the content key wrapped
/// once per device that should be able to open them.
/// </summary>
public class SealPaymentHandlesDto
{
    /// <summary>The sealed handle set. Base64 over the wire; opaque on both sides of that.</summary>
    public required byte[] Ciphertext { get; set; }

    public required byte[] Nonce { get; set; }

    /// <summary>The crypto envelope the client used, so the scheme can be rotated later without
    /// every existing row having to be guessed at.</summary>
    public int Version { get; set; } = 1;

    /// <summary>One entry per device that should be able to open <see cref="Ciphertext"/>,
    /// including the author's own other devices. An empty list is legal and means "sealed to
    /// nobody yet" - a client that has not fetched the recipient roster should still be able to
    /// store its own details.</summary>
    public List<PaymentHandleWrapDto> Wraps { get; set; } = [];
}

public class PaymentHandleWrapDto
{
    public required string RecipientUserId { get; set; }

    /// <summary>Client device id, matching what that device sends as <c>X-Device-Id</c>.</summary>
    public required string RecipientDeviceId { get; set; }

    /// <summary>The content key sealed to that device's public key. Opaque.</summary>
    public required byte[] WrappedKey { get; set; }
}

/// <summary><see cref="CreateExpenseDto"/> plus the spending category.</summary>
public class CreateCategorizedExpenseDto : CreateExpenseDto
{
    public ExpenseCategory? Category { get; set; }
}

/// <summary><see cref="UpdateExpenseDto"/> plus the spending category.</summary>
public class UpdateCategorizedExpenseDto : UpdateExpenseDto
{
    public ExpenseCategory? Category { get; set; }
}
