namespace Guild.Domain.Entity;

/// <summary>One-to-one with Guild, upserted like GuildAutoModConfig - "the config" always exists
/// conceptually, defaulting to disabled.
///
/// Quiet hours exist because a household app that pushes "the bins are due" at 23:50 gets muted in
/// week two, and a muted app stops being useful for the things that matter.</summary>
public class GuildQuietHoursConfig
{
    public string GuildId { get; set; } = null!;
    public virtual Aggregates.Guild Guild { get; set; } = null!;

    public bool Enabled { get; set; }

    /// <summary>Minutes past local midnight, 0-1439. The window wraps when Start > End, which is
    /// the normal case (22:00 -> 07:00) rather than an edge case.</summary>
    public int StartMinuteLocal { get; set; } = 22 * 60;

    public int EndMinuteLocal { get; set; } = 7 * 60;

    /// <summary>IANA id ("Europe/Zurich"). Stored per guild, not per member: quiet hours are a
    /// property of the house, and a flatmate in another timezone is not the case worth modelling.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The next instant at or after <paramref name="instant"/> that falls outside the
    /// quiet window, or <paramref name="instant"/> itself when it is already outside (or quiet
    /// hours are off). Used to push chore reminders to the end of the window.</summary>
    public DateTimeOffset DeferPast(DateTimeOffset instant)
    {
        if (!Enabled) return instant;

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // A bad tz id must not stop a reminder from ever firing - degrade to "no quiet
            // hours" rather than deferring into the void.
            return instant;
        }

        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var minute = local.Hour * 60 + local.Minute;

        var inWindow = StartMinuteLocal <= EndMinuteLocal
            ? minute >= StartMinuteLocal && minute < EndMinuteLocal
            : minute >= StartMinuteLocal || minute < EndMinuteLocal;   // wraps midnight

        if (!inWindow) return instant;

        // Advance to EndMinuteLocal, rolling to tomorrow when the window wrapped past midnight.
        var endToday = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset)
            .AddMinutes(EndMinuteLocal);
        if (endToday <= local) endToday = endToday.AddDays(1);

        return endToday.ToUniversalTime();
    }
}
