namespace Messaging.Domain.Enums;

public enum MlsGenerationState
{
    /// <summary>Encryption is on for this context and this is the group carrying it. At most one
    /// generation per context is Active at a time.</summary>
    Active,

    /// <summary>Encryption was switched off. The group's messages remain ciphertext; the generation
    /// row is kept so they can still be attributed to a group and decrypted by devices that hold it.</summary>
    Terminated,
}
