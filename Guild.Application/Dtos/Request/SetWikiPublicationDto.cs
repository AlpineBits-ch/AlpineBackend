namespace Guild.Application.Dtos.Request;

public class SetWikiPublicationDto
{
    /// <summary>The slug to publish the wiki on, or null/empty to take it off the public host.</summary>
    public string? Slug { get; set; }
}

public class SetWikiPagePublicationDto
{
    /// <summary>Whether this page is served to anonymous readers.</summary>
    public bool Published { get; set; }
}
