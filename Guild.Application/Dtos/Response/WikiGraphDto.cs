namespace Guild.Application.Dtos.Response;

/// <summary>
/// The page graph of one wiki. Hand-written rather than a Facet for the same reason as
/// <see cref="WikiDto"/>: no single entity backs it.
/// </summary>
public class WikiGraphDto
{
    /// <summary>Every page the caller may see.</summary>
    public List<WikiGraphNodeDto> Nodes { get; set; } = [];

    /// <summary>The body links between those pages. The page tree is not in here: it is
    /// <see cref="WikiGraphNodeDto.ParentPageId"/>.</summary>
    public List<WikiGraphEdgeDto> Edges { get; set; } = [];
}

/// <summary>One page in the graph.</summary>
public class WikiGraphNodeDto
{
    public required string Id { get; init; }
    public required string Title { get; init; }

    /// <summary>A single emoji shown next to the title, or null.</summary>
    public string? Icon { get; init; }

    public string? ParentPageId { get; init; }
    public string? CategoryId { get; init; }
}

/// <summary>One <c>wiki:</c> link from one page body to another.</summary>
public class WikiGraphEdgeDto
{
    public required string SourcePageId { get; init; }
    public required string TargetPageId { get; init; }

    /// <summary>The heading slug the link points at, or null for the page itself.</summary>
    public string? HeadingId { get; init; }
}
