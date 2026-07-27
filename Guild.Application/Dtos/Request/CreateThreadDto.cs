namespace Guild.Application.Dtos.Request;

public class CreateThreadDto
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
}
