namespace Guild.Domain.Enums;

public enum EncryptionState
{
    /// <summary>
    /// Messages are not encrypted in this channel or server.
    /// </summary>
    Plain,
    /// <summary>
    /// Messages are encrypted in this channel or server.
    /// </summary>
    Encrypted,
    /// <summary>
    /// Messages are encrypted in this channel or server, but the fallback key in the user is not being used. Thats important if you want to limit breach risk in case of a compromised
    /// fallback key.
    /// </summary>
    EncryptedWithoutFallbackKey
}