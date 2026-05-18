using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

public class CreateInviteDto
{
    public InviteType Type { get; set; }
}