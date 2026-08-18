namespace Guild.Domain.Enums;

/// <summary>Member type.</summary>
public enum MemberType
{
    /// <summary>This represents a real user.</summary>
    Default,
    Bot,

    /// <summary>Unused, and it must stay that way: a persona is an entity of its own and never a
    /// member row, for the reasons in docs/specs/roleplay-guilds.md §2. Postgres will not drop an
    /// enum value cleanly, which is the only reason this member is still here.</summary>
    Persona
}