namespace Guild.Application.Dtos.Request;

public class UpdateWikiCategoryDto
{
    public string? Name { get; set; }
    public int? Position { get; set; }
    public string? ParentCategoryId { get; set; }
}
