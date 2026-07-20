namespace Isle.Contracts.Dtos;

public class PlayerDto
{
    public string Id {get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string SteamId { get; set; }
    public string? UserId { get; set; }
    public long Xp { get; set; }
}