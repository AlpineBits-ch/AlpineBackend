namespace Guild.Application.Dtos.Response;

/// <summary>One emoji's aggregate on a page.</summary>
public class WikiReactionDto
{
    public string Emoji { get; set; } = string.Empty;

    /// <summary>How many distinct users used this emoji on this page.</summary>
    public int Count { get; set; }

    /// <summary>Whether the calling user is one of them.</summary>
    public bool Me { get; set; }
}
