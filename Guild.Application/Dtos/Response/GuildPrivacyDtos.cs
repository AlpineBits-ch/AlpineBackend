using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

/// <summary>One guild's DM toggle as the owning user sees it.</summary>
public class GuildDirectMessagePreferenceDto
{
    public required string GuildId { get; init; }

    public required bool AllowDirectMessages { get; init; }

    /// <summary>False when the value shown is inherited from the account-level
    /// <c>DirectMessagePolicy</c> rather than stored for this guild. The client needs the
    /// distinction to render "following your global setting" instead of an explicit toggle -
    /// without it, a user cannot tell that changing the global policy will move this one too.</summary>
    public required bool IsOverride { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public static GuildDirectMessagePreferenceDto From(GuildDirectMessagePreference preference) => new()
    {
        GuildId = preference.GuildId,
        AllowDirectMessages = preference.AllowDirectMessages,
        IsOverride = true,
        UpdatedAt = preference.UpdatedAt,
    };
}
