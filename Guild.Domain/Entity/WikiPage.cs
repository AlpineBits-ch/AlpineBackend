using System.ComponentModel.DataAnnotations.Schema;
using Domain;
using Guild.Domain.Enums;
using Guild.Domain.Events.Wiki;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateWikiPageParams
{
    public string GuildId { get; init; }
    public string Title { get; init; }
    public string Content { get; init; } = string.Empty;
    public string AuthorId { get; init; }
    public string? ParentPageId { get; init; }
    public string? CategoryId { get; init; }
    public WikiVisibility Visibility { get; init; } = WikiVisibility.Public;
    public List<string> Tags { get; init; } = [];
    public bool IsPinned { get; init; }
}

public class WikiPage : Aggregate<WikiPage>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "wkpg";

    public string GuildId { get; set; }
    public string Title { get; set; }
    public string Slug { get; set; }
    public string Content { get; set; }
    public string AuthorId { get; set; }
    public string? LastEditorId { get; set; }
    public string? ParentPageId { get; set; }
    public string? CategoryId { get; set; }
    public WikiVisibility Visibility { get; set; } = WikiVisibility.Public;
    public List<string> Tags { get; set; } = [];
    public bool IsPinned { get; set; }

    public virtual ICollection<WikiRevision> Revisions { get; set; } = [];

    public static WikiPage Create(CreateWikiPageParams @params)
    {
        var id = GenerateId();
        var date = DateTime.UtcNow;
        var page = new WikiPage
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,
            GuildId = @params.GuildId,
            Title = @params.Title,
            Slug = BuildSlug(@params.Title, id),
            Content = @params.Content,
            AuthorId = @params.AuthorId,
            ParentPageId = @params.ParentPageId,
            CategoryId = @params.CategoryId,
            Visibility = @params.Visibility,
            Tags = @params.Tags,
            IsPinned = @params.IsPinned,
        };
        page.Revisions.Add(WikiRevision.Create(new CreateWikiRevisionParams
        {
            PageId = id,
            Content = @params.Content,
            EditorId = @params.AuthorId,
            RevisionNumber = 1,
        }));
        page.AddDomainEvent(new WikiPageCreated { PageId = id, GuildId = @params.GuildId });
        return page;
    }

    public void RaiseUpdated() =>
        AddDomainEvent(new WikiPageUpdated { PageId = Id, GuildId = GuildId });

    public void RaiseDeleted() =>
        AddDomainEvent(new WikiPageDeleted { PageId = Id, GuildId = GuildId });

    private static string BuildSlug(string title, string id)
    {
        var slug = new string(
                title.ToLowerInvariant()
                    .Replace(" ", "-")
                    .Where(c => char.IsLetterOrDigit(c) || c == '-')
                    .ToArray())
            .Trim('-');
        return $"{slug}-{id[^8..].ToLowerInvariant()}";
    }
}
