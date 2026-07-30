using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

public class UpdateGuildDto
{
    public string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Channel that receives join/leave system messages - Overview-style setting in
    /// Discord. Null/omitted leaves the guild's current system channel untouched (not cleared),
    /// so older clients that don't send this field can't accidentally unset it.</summary>
    public string? SystemChannelId { get; set; }

    /// <summary>Null/omitted leaves the guild's current verification level untouched.</summary>
    public GuildVerificationLevel? VerificationLevel { get; set; }
}
