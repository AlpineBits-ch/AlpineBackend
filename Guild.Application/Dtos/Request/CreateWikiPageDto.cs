using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

public class CreateWikiPageDto
{
    public required string Title { get; set; }
    public string? Content { get; set; }
    public string? ParentPageId { get; set; }
    public string? CategoryId { get; set; }
    public WikiVisibility? Visibility { get; set; }
    public List<string>? Tags { get; set; }
    public bool? IsPinned { get; set; }

    /// <summary>A single emoji shown next to the title.</summary>
    public string? Icon { get; set; }

    /// <summary>URL of an already-uploaded cover image.</summary>
    public string? CoverUrl { get; set; }
}
