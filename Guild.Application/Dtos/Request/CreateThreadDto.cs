namespace Guild.Application.Dtos.Request;

public class CreateThreadDto
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }

    /// <summary>Initial post body - required in practice for a Forum-parented thread (a "forum
    /// post" with no message would just be an empty title), optional for a Text-parented one.</summary>
    public string? Content { get; set; }

    /// <summary>Forum tags to apply to the new post. Ignored for a Text-parented thread (those
    /// have no tag vocabulary); required for a Forum-parented one if the forum sets RequireTag.</summary>
    public List<string> TagIds { get; set; } = [];
}
