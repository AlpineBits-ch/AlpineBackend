namespace Guild.Application.Dtos.Request;

public class CreateGuildTemplateFromGuildDto
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
}

public class CreateGuildFromTemplateDto
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
}
