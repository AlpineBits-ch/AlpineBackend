namespace Identity.Domain.Enums;

/// <summary>The rights a data-subject request can exercise (T1-13).</summary>
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
