namespace Messaging.Domain.Enums;

public enum AuthorIdType
{
    User,
    Bot,
    Webhook,

    /// <summary>A user speaking as one of their characters - AuthorId still names the real account,
    /// and only the display overrides and PersonaId carry the costume.</summary>
    Persona
}