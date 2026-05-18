namespace Guild.Application.Dtos.Response;

public class WikiDto
{
    public string Id { get; set; }
    public string GuildId { get; set; }
    public List<WikiCategoryDto> Categories { get; set; } = [];
    public List<WikiPageSummaryDto> Pages { get; set; } = [];
}
