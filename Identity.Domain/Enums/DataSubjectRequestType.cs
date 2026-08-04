namespace Identity.Domain.Enums;

/// <summary>
/// The rights a data-subject request can exercise (T1-13). Named for the GDPR articles they come
/// from rather than for the internal machinery that services them, because the queue exists to
/// answer requests that arrive by email and post - from people who may not have an account here at
/// all - and the wording on the request is what has to be matched.
/// </summary>
public enum DataSubjectRequestType
{
    /// <summary>Art. 15 - a copy of the personal data held.</summary>
    Access,

    /// <summary>Art. 17 - erasure ("right to be forgotten").</summary>
    Erasure,

    /// <summary>Art. 16 - correction of inaccurate data.</summary>
    Rectification,

    /// <summary>Art. 20 - a machine-readable export, transferable elsewhere.</summary>
    Portability,

    /// <summary>Art. 21 - objection to a particular processing activity.</summary>
    Objection,
}
