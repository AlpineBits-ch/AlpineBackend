using Echo.Entitlements.Model;

namespace Echo.Entitlements.Keys;

/// <summary>The ladders referenced by the catalogue below.</summary>
public static class EntitlementLadders
{
    /// <summary>What a publisher may send and what the SFU will therefore fan out.</summary>
    public static readonly EntitlementLadder VideoQuality =
        new("video_quality", "none", "480p30", "720p30", "1080p30", "1080p60", "1440p60", "2160p60");
}

/// <summary>
/// The single source of truth for "which entitlement keys exist, what shape each one is, whose
/// sources may set it, and what it means when nobody does".
/// </summary>
public static class EntitlementKeys
{
    /// <summary>How many people may be in one voice room.</summary>
    public static readonly EntitlementKey VoiceMaxParticipants =
        EntitlementKey.Numeric("voice.max_participants", EntitlementScope.Guild, EntitlementValue.Unlimited);

    /// <summary>The publish ceiling for camera and screenshare.</summary>
    public static readonly EntitlementKey VoiceVideoCeiling =
        EntitlementKey.OnLadder("voice.video_ceiling", EntitlementScope.Paired,
            EntitlementLadders.VideoQuality,
            EntitlementLadders.VideoQuality.RungAt(EntitlementLadders.VideoQuality.HighestRank));

    /// <summary>Concurrent video publishers in one room.</summary>
    public static readonly EntitlementKey VoiceMaxPublishers =
        EntitlementKey.Numeric("voice.max_publishers", EntitlementScope.Guild, EntitlementValue.Unlimited);

    /// <summary>Largest single upload into a guild.</summary>
    public static readonly EntitlementKey StorageUploadMaxBytes =
        EntitlementKey.Numeric("storage.upload_max_bytes", EntitlementScope.Paired, EntitlementValue.Unlimited);

    /// <summary>Total bytes a guild may hold.</summary>
    public static readonly EntitlementKey StorageGuildQuotaBytes =
        EntitlementKey.Numeric("storage.guild_quota_bytes", EntitlementScope.Guild, EntitlementValue.Unlimited);

    public static readonly EntitlementKey GuildEmojiSlots =
        EntitlementKey.Numeric("guild.emoji_slots", EntitlementScope.Guild, EntitlementValue.Unlimited);

    public static readonly EntitlementKey GuildBotsInstalled =
        EntitlementKey.Numeric("guild.bots_installed", EntitlementScope.Guild, EntitlementValue.Unlimited);

    public static readonly EntitlementKey GuildVanityUrl =
        EntitlementKey.Flag("guild.vanity_url", EntitlementScope.Guild, false);

    /// <summary>How far back the audit log is readable.</summary>
    public static readonly EntitlementKey GuildAuditLogDays =
        EntitlementKey.Numeric("guild.audit_log_days", EntitlementScope.Guild, EntitlementValue.Unlimited);

    /// <summary>
    /// Largest single upload where no guild is involved - direct messages, avatars, anything the
    /// person does as themselves.
    /// </summary>
    public static readonly EntitlementKey UserUploadMaxBytes =
        EntitlementKey.Numeric("user.upload_max_bytes", EntitlementScope.User, EntitlementValue.Unlimited);

    /// <summary>Registered devices per account.</summary>
    public static readonly EntitlementKey UserMaxDevices =
        EntitlementKey.Numeric("user.max_devices", EntitlementScope.User, EntitlementValue.Unlimited);

    /// <summary>Every key there is.</summary>
    public static readonly IReadOnlyList<EntitlementKey> All =
    [
        VoiceMaxParticipants,
        VoiceVideoCeiling,
        VoiceMaxPublishers,
        StorageUploadMaxBytes,
        StorageGuildQuotaBytes,
        GuildEmojiSlots,
        GuildBotsInstalled,
        GuildVanityUrl,
        GuildAuditLogDays,
        UserUploadMaxBytes,
        UserMaxDevices,
    ];

    private static readonly Dictionary<string, EntitlementKey> ByNameIndex =
        All.ToDictionary(key => key.Name, StringComparer.Ordinal);

    public static bool TryGet(string name, out EntitlementKey key) =>
        ByNameIndex.TryGetValue(name, out key!);

    /// <summary>The key with this name, or a failure naming the alternatives.</summary>
    public static EntitlementKey Require(string name) =>
        TryGet(name, out var key)
            ? key
            : throw new KeyNotFoundException(
                $"'{name}' is not an entitlement key. Known keys: {string.Join(", ", All.Select(k => k.Name))}.");

    /// <summary>The keys a subject of this kind can hold, which is its own scope plus the paired
    /// ones. Used by the resolver and by the client-facing "my entitlements" endpoint.</summary>
    public static IEnumerable<EntitlementKey> For(SubjectKind subject) =>
        All.Where(key => key.AppliesTo(subject));
}
