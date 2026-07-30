namespace Guild.Application.Dtos.Request;

public class CreateThreadDto
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }

    /// <summary>Initial post body - required in practice for a Forum-parented thread (a "forum
    /// post" with no message would just be an empty title), optional for a Text-parented one.</summary>
    public string? Content { get; set; }
}
