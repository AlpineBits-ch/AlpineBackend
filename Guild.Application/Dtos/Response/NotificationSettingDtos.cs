using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Response;

public class GuildNotificationSettingDto
{
    public string GuildId { get; set; } = null!;
    public NotificationLevel Level { get; set; }
    public DateTimeOffset? MutedUntil { get; set; }
    public bool SuppressEveryone { get; set; }
    public bool SuppressRoleMentions { get; set; }
    public bool MobilePush { get; set; }

    public List<NotificationOverrideDto> Overrides { get; set; } = [];

    public static GuildNotificationSettingDto From(string guildId, GuildNotificationSetting? setting, List<NotificationOverride> overrides) => new()
    {
        GuildId = guildId,
        // A member who has never touched their settings has no row; the response still describes
        // the defaults they are effectively on, so the client never has to special-case "absent".
        Level = setting?.Level ?? NotificationResolutionService.Default.Level,
        MutedUntil = setting?.MutedUntil,
        SuppressEveryone = setting?.SuppressEveryone ?? NotificationResolutionService.Default.SuppressEveryone,
        SuppressRoleMentions = setting?.SuppressRoleMentions ?? NotificationResolutionService.Default.SuppressRoleMentions,
        MobilePush = setting?.MobilePush ?? NotificationResolutionService.Default.MobilePush,
        Overrides = overrides.Select(NotificationOverrideDto.From).ToList(),
    };
}

public class NotificationOverrideDto
{
    public string? ChannelId { get; set; }
    public string? CategoryId { get; set; }
    public NotificationLevel? Level { get; set; }
    public DateTimeOffset? MutedUntil { get; set; }

    public static NotificationOverrideDto From(NotificationOverride o) => new()
    {
        ChannelId = o.ChannelId,
        CategoryId = o.CategoryId,
        Level = o.Level,
        MutedUntil = o.MutedUntil,
    };
}
