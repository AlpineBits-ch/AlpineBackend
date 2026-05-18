namespace Guild.Application.Dtos.Request;

public class CreateWikiCategoryDto
{
    public required string Name { get; set; }
    public int? Position { get; set; }
    public string? ParentCategoryId { get; set; }
}
