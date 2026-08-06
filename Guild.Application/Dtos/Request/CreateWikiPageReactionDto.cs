namespace Guild.Application.Dtos.Request;

public class CreateWikiPageReactionDto
{
    /// <summary>A single unicode emoji.</summary>
    public required string Emoji { get; set; }
}
