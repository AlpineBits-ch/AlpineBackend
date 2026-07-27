namespace Guild.Application.Dtos.Request;

public class CreateBanDto
{
    public string UserId { get; set; } = null!;
    public string? Reason { get; set; }
}
