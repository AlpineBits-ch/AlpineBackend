using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

public class CreateInviteDto
{
    public InviteType Type { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>How many times this invite may be redeemed before it expires on its own.</summary>
    public int? MaxUses { get; set; }

    /// <summary>Where the joining client should land.</summary>
    public string? ChannelId { get; set; }

    /// <summary>Temporary membership - see <see cref="Domain.Entity.GuildInvite.Temporary"/>.</summary>
    public bool Temporary { get; set; }

    public InviteTargetType TargetType { get; set; } = InviteTargetType.None;

    /// <summary>Who the redeemer is being invited to join.</summary>
    public string? TargetUserId { get; set; }
}
