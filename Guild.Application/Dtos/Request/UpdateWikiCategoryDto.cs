namespace Guild.Application.Dtos.Request;

public class UpdateWikiCategoryDto
{
    public string? Name { get; set; }
    public int? Position { get; set; }
    public string? ParentCategoryId { get; set; }

    /// <summary>Omit to leave the template alone, send <c>null</c> to remove it.</summary>
    public Optional<string> InfoboxTemplateJson { get; set; }
}
