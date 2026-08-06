using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

/// <summary>
/// A partial update: every property is optional, and an omitted property leaves the page's current
/// value alone.
/// </summary>
public class UpdateWikiPageDto
{
    public string? Title { get; set; }
    public string? Content { get; set; }

    /// <summary>Omit to leave the parent alone, send <c>null</c> to move the page to the top level,
    /// send an id to reparent it.</summary>
    public Optional<string> ParentPageId { get; set; }

    /// <summary>Omit to leave the category alone, send <c>null</c> to remove the page from its
    /// category, send an id to move it.</summary>
    public Optional<string> CategoryId { get; set; }

    public WikiVisibility? Visibility { get; set; }
    public List<string>? Tags { get; set; }
    public bool? IsPinned { get; set; }

    /// <summary>
    /// Optional note describing what changed, stored on the revision this update creates.
    /// </summary>
    public string? Summary { get; set; }
}
