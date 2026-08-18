namespace Guild.Domain.Enums;

/// <summary>What a guild is, as picked once at creation.</summary>
public enum GuildKind
{
    Community,
    Household,
    Team,
    Study,
    Event,

    /// <summary>Text roleplay: characters, scenes, dice and a chronicle.</summary>
    Roleplay,
}
