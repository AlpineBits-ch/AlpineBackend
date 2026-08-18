namespace Guild.Application.Dtos.Request;

public class CreateWikiCategoryDto
{
    public required string Name { get; set; }
    public int? Position { get; set; }
    public string? ParentCategoryId { get; set; }

    /// <summary>The infobox template pages in this category use: field list, types, required
    /// flags. Opaque JSON to every consumer except the infobox renderer.</summary>
    public string? InfoboxTemplateJson { get; set; }
}
