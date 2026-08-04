namespace Identity.Domain.Enums;

/// <summary>
/// The kinds of legal document an account can consent to (T1-10).
///
/// <para><c>Terms</c> and <c>Privacy</c> are the two recorded automatically at registration and the
/// two that can put an account into a "consent required" state. <c>Cookies</c> exists because a
/// cookie notice is a separate acceptance in the jurisdictions that require one at all, and folding
/// it into the privacy policy would make a withdrawal of cookie consent read as a withdrawal of the
/// privacy policy - which is not withdrawable while the account is active.</para>
/// </summary>
public enum LegalDocumentType
{
    Terms,
    Privacy,
    Cookies,
}
