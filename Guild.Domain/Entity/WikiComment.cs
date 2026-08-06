using System.ComponentModel.DataAnnotations.Schema;
using Domain;
using Guild.Domain.Events.Wiki;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateWikiCommentParams
{
    public string PageId { get; init; }
    public string GuildId { get; init; }
    public string AuthorId { get; init; }
    public string Content { get; init; }
}

/// <summary>A comment on a wiki page.</summary>
public class WikiComment : Aggregate<WikiComment>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "wkcm";

    public string PageId { get; set; }
    public string GuildId { get; set; }
    public string AuthorId { get; set; }
    public string Content { get; set; }

    /// <summary>Set the first time the author edits.</summary>
    public DateTime? EditedAt { get; set; }

    public static WikiComment Create(CreateWikiCommentParams @params)
    {
        var content = @params.Content?.Trim() ?? string.Empty;
        if (content.Length == 0)
            throw new ArgumentException("Comment content must not be empty", nameof(@params.Content));

        var id = GenerateId();
        var date = DateTime.UtcNow;
        var comment = new WikiComment
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,
            PageId = @params.PageId,
            GuildId = @params.GuildId,
            AuthorId = @params.AuthorId,
            Content = content,
        };
        comment.AddDomainEvent(new WikiCommentCreated
        {
            CommentId = id, PageId = @params.PageId, GuildId = @params.GuildId, AuthorId = @params.AuthorId,
        });
        return comment;
    }

    public void Edit(string content)
    {
        var trimmed = content?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new ArgumentException("Comment content must not be empty", nameof(content));

        Content = trimmed;
        EditedAt = DateTime.UtcNow;
    }

    public void RaiseUpdated() =>
        AddDomainEvent(new WikiCommentUpdated { CommentId = Id, PageId = PageId, GuildId = GuildId });

    public void RaiseDeleted() =>
        AddDomainEvent(new WikiCommentDeleted { CommentId = Id, PageId = PageId, GuildId = GuildId });
}
