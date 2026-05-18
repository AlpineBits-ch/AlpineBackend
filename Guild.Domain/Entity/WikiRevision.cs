using System.ComponentModel.DataAnnotations.Schema;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateWikiRevisionParams
{
    public string PageId { get; init; }
    public string Content { get; init; }
    public string EditorId { get; init; }
    public int RevisionNumber { get; init; }
    public string? Summary { get; init; }
}

public class WikiRevision : BaseEntity<WikiRevision>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "wkrv";

    public string PageId { get; set; }
    public string Content { get; set; }
    public string EditorId { get; set; }
    public int RevisionNumber { get; set; }
    public string? Summary { get; set; }

    public static WikiRevision Create(CreateWikiRevisionParams @params)
    {
        var id = GenerateId();
        var date = DateTime.UtcNow;
        return new WikiRevision
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,
            PageId = @params.PageId,
            Content = @params.Content,
            EditorId = @params.EditorId,
            RevisionNumber = @params.RevisionNumber,
            Summary = @params.Summary,
        };
    }
}
