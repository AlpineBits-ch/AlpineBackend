namespace Identity.Application.Dtos.Response;

/// <summary>
/// What a completed password reset did to the account's encrypted state.
///
/// <para>The reset endpoint used to return a bare <c>Ok()</c>, and the master key silently became
/// unopenable behind it. These two flags are the whole difference between a user who re-wraps on
/// next unlock and a user who discovers months later that every backup they made is unreadable.</para>
/// </summary>
public class ResetPasswordResultDto
{
    /// <summary>
    /// The client must unlock from the recovery code and re-wrap the master key under the new
    /// password (<c>POST api/v1/backup/recovery-key/rewrap-password</c>).
    ///
    /// <para>Until it does, the new password opens nothing. Nothing is lost yet - the recovery-code
    /// wrapping still holds the key - but the account is one forgotten recovery code away from the
    /// state below.</para>
    /// </summary>
    public bool MasterKeyRewrapRequired { get; set; }

    /// <summary>
    /// False when the reset has already made the encrypted history permanently unreadable: the
    /// password wrapping is gone and there was no recovery-code wrapping to fall back on.
    ///
    /// <para>Not a warning about the future. Surface it as a completed loss - every backup blob on
    /// the account is unopenable from this moment, and no amount of correct passwords will change
    /// that.</para>
    /// </summary>
    public bool EncryptedHistoryRecoverable { get; set; }
}
