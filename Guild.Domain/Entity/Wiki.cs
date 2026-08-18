using System.ComponentModel.DataAnnotations.Schema;
using Persistence;

namespace Guild.Domain.Entity;

public class Wiki : BaseEntity<Wiki>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "wiki";

    public string GuildId { get; set; }

    /// <summary>The guild-level opt-in to public hosting: the slug this wiki answers on, or null
    /// when the guild is not on the public wiki host at all.</summary>
    public string? PublishedSlug { get; set; }

    /// <summary>When <see cref="PublishedSlug"/> was last claimed.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    public static Wiki Create(string guildId)
    {
        var id = GenerateId();
        return new Wiki
        {
            Id = id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            GuildId = guildId,
        };
    }

    /// <summary>Claims a public slug for this wiki.</summary>
    /// <param name="slug">The already-normalised slug.</param>
    public void Publish(string slug)
    {
        PublishedSlug = slug;
        PublishedAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Takes the whole wiki off the public host. Per-page flags are left alone, the same way
    /// clearing a vanity URL leaves its backing invite alone: this is the master switch, and turning
    /// it back on is a deliberate act by somebody holding the permission.
    /// </summary>
    public void Unpublish()
    {
        PublishedSlug = null;
        PublishedAt = null;
        UpdatedAt = DateTime.UtcNow;
    }
}
