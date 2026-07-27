using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

public class CreateInviteDto
{
    public InviteType Type { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int? MaxUses { get; set; }
    public string? ChannelId { get; set; }
}