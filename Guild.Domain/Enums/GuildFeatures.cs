namespace Guild.Domain.Enums;

/// <summary>Which modules a guild actually has.</summary>
[Flags]
public enum GuildFeatures : ulong
{
    None            = 0,

    // ── Structural ───────────────────────────────────────────────────────────
    VoiceChannels   = 1ul << 0,
    Threads         = 1ul << 1,
    Forums          = 1ul << 2,
    Announcements   = 1ul << 3,
    Tickets         = 1ul << 4,

    // ── Community scale ──────────────────────────────────────────────────────
    Moderation      = 1ul << 5,
    AutoMod         = 1ul << 6,
    Onboarding      = 1ul << 7,
    Emojis          = 1ul << 8,
    Bots            = 1ul << 9,
    Wiki            = 1ul << 10,
    Events          = 1ul << 11,

    // ── Household / shared living ────────────────────────────────────────────
    Lists           = 1ul << 20,
    Chores          = 1ul << 21,
    Ledger          = 1ul << 22,
    Pantry          = 1ul << 23,
    Decisions       = 1ul << 24,
    Presence        = 1ul << 25,
    QuietHours      = 1ul << 26,
    GuestAccess     = 1ul << 27,
}

public static class GuildFeaturePresets
{
    /// <summary>Everything this service shipped before feature gating existed.</summary>
    public const GuildFeatures Community =
        GuildFeatures.VoiceChannels | GuildFeatures.Threads | GuildFeatures.Forums |
        GuildFeatures.Announcements | GuildFeatures.Tickets | GuildFeatures.Moderation |
        GuildFeatures.AutoMod | GuildFeatures.Onboarding | GuildFeatures.Emojis |
        GuildFeatures.Bots | GuildFeatures.Wiki | GuildFeatures.Events;

    /// <summary>Four people sharing a flat do not ban each other, do not need automod, and never
    /// saw a rules-acceptance screen. Moderation/AutoMod/Onboarding/Forums/Tickets/Bots are off
    /// on purpose - the move-out flow replaces kick/ban here.</summary>
    public const GuildFeatures Household =
        GuildFeatures.VoiceChannels | GuildFeatures.Threads | GuildFeatures.Wiki |
        GuildFeatures.Events | GuildFeatures.Lists | GuildFeatures.Chores |
        GuildFeatures.Ledger | GuildFeatures.Pantry | GuildFeatures.Decisions |
        GuildFeatures.Presence | GuildFeatures.QuietHours | GuildFeatures.GuestAccess;

    public const GuildFeatures Team =
        GuildFeatures.VoiceChannels | GuildFeatures.Threads | GuildFeatures.Forums |
        GuildFeatures.Announcements | GuildFeatures.Moderation | GuildFeatures.Emojis |
        GuildFeatures.Bots | GuildFeatures.Wiki | GuildFeatures.Events |
        GuildFeatures.Lists | GuildFeatures.Decisions | GuildFeatures.Presence;

    public const GuildFeatures Study =
        GuildFeatures.VoiceChannels | GuildFeatures.Threads | GuildFeatures.Forums |
        GuildFeatures.Moderation | GuildFeatures.Wiki | GuildFeatures.Events |
        GuildFeatures.Lists | GuildFeatures.Decisions;

    public const GuildFeatures Event =
        GuildFeatures.VoiceChannels | GuildFeatures.Threads | GuildFeatures.Announcements |
        GuildFeatures.Moderation | GuildFeatures.Onboarding | GuildFeatures.Events |
        GuildFeatures.Lists | GuildFeatures.Ledger | GuildFeatures.Decisions;

    public static GuildFeatures For(GuildKind kind) => kind switch
    {
        GuildKind.Household => Household,
        GuildKind.Team => Team,
        GuildKind.Study => Study,
        GuildKind.Event => Event,
        _ => Community,
    };
}
