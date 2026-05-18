namespace Guild.Domain.Enums;

/// <summary>
/// Member type.
/// </summary>
public enum MemberType
{
    /// <summary>
    /// This represents a real user.
    /// </summary>
    Default,
    Bot,
    /// <summary>
    /// Used for personas, that can be created by user. The user will then be able to change into a persona
    /// </summary>
    Persona
}