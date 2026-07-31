namespace Messaging.Domain.Enums;

public enum MlsGenerationState
{
    /// <summary>Encryption is on for this context and this is the group carrying it.</summary>
    Active,

    /// <summary>Encryption was switched off.</summary>
    Terminated,
}
