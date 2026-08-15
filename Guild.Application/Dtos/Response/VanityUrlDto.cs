namespace Guild.Application.Dtos.Response;

/// <summary>A guild's vanity URL and whether it currently does anything.</summary>
public class VanityUrlDto
{
    public required string GuildId { get; init; }

    /// <summary>The claimed slug, lowercase, or null if the guild has none.</summary>
    public string? VanityUrl { get; init; }

    /// <summary>Whether the guild's plan currently covers vanity URLs.</summary>
    public bool Entitled { get; init; }

    /// <summary>Whether the link works right now.</summary>
    public bool Active { get; init; }

    public DateTimeOffset? SetAt { get; init; }
}
