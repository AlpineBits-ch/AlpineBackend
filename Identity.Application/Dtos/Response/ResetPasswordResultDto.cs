namespace Identity.Application.Dtos.Response;

/// <summary>What a completed password reset did to the account's encrypted state.</summary>
public class ResetPasswordResultDto
{
    /// <summary>
    /// The client must unlock from the recovery code and re-wrap the master key under the new
    /// password (<c>POST api/v1/backup/recovery-key/rewrap-password</c>).
    /// </summary>
    public bool MasterKeyRewrapRequired { get; set; }

    /// <summary>
    /// False when the reset has already made the encrypted history permanently unreadable: the
    /// password wrapping is gone and there was no recovery-code wrapping to fall back on.
    /// </summary>
    public bool EncryptedHistoryRecoverable { get; set; }

    /// <summary>
    /// Single-use permit for <c>POST api/v1/backup/recovery-key/rewrap-password</c>, present only
    /// when <see cref="MasterKeyRewrapRequired"/> is true.
    /// </summary>
    public string? MasterKeyRewrapTicket { get; set; }
}
