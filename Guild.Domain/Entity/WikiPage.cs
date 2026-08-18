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
    public string? Icon { get; init; }
    public string? CoverUrl { get; init; }
    public string? PersonaId { get; init; }
    public string? InfoboxJson { get; init; }
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

    /// <summary>A single emoji shown next to the page title.</summary>
    public string? Icon { get; set; }

    /// <summary>
    /// The per-page opt-in to public hosting, and the moment somebody chose it. Null on every page
    /// that existed before publishing did, which is the point: <see cref="Visibility"/> has always
    /// defaulted to Public meaning "visible to the guild", so it cannot be what grants the open
    /// internet access.
    /// </summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Absolute or app-relative URL of an already-uploaded cover image.</summary>
    public string? CoverUrl { get; set; }

    /// <summary>Set when this page is a character page, unique per guild so one persona has one
    /// page here. There is no separate character-sheet entity; the prose and the stats are this
    /// page and its infobox.</summary>
    public string? PersonaId { get; set; }

    /// <summary>The structured half of the page, shaped by the category's infobox template. Real
    /// jsonb rather than opaque text: unlike <c>Message.EmbedsJson</c> this only ever lives in
    /// Postgres, so it can stay queryable and dice can read <c>@sheet.perception</c> off it.</summary>
    public string? InfoboxJson { get; set; }

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
            Icon = @params.Icon,
            CoverUrl = @params.CoverUrl,
            PersonaId = @params.PersonaId,
            InfoboxJson = @params.InfoboxJson,
        };
        page.Revisions.Add(WikiRevision.Create(new CreateWikiRevisionParams
        {
            PageId = id,
            Content = @params.Content,
            InfoboxJson = @params.InfoboxJson,
            EditorId = @params.AuthorId,
            RevisionNumber = 1,
        }));
        page.AddDomainEvent(new WikiPageCreated { PageId = id, GuildId = @params.GuildId });
        return page;
    }

    /// <param name="editorId">Who made the change.</param>
    public void RaiseUpdated(string? editorId = null) =>
        AddDomainEvent(new WikiPageUpdated { PageId = Id, GuildId = GuildId, EditorId = editorId });

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
