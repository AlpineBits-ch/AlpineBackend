using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

public class UpdateGuildNotificationSettingDto
{
    public NotificationLevel? Level { get; set; }

    /// <summary>Minutes to mute for.</summary>
    public int? MuteMinutes { get; set; }

    /// <summary>"Until I turn it back on" - stored as a far-future timestamp so every consumer
    /// still only compares one field against now.</summary>
    public bool MuteForever { get; set; }

    public bool? SuppressEveryone { get; set; }
    public bool? SuppressRoleMentions { get; set; }
    public bool? MobilePush { get; set; }
}

/// <summary>Channel and category overrides take the same body - only the route differs.</summary>
public class UpdateNotificationOverrideDto
{
    /// <summary>Null means "inherit", which is a real value here and not merely an omission -
    /// clients send an explicit null to drop back to the guild/category level while keeping a
    /// mute in place.</summary>
    public NotificationLevel? Level { get; set; }

    public int? MuteMinutes { get; set; }
    public bool MuteForever { get; set; }
}
