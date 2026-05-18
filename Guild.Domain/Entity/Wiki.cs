using System.ComponentModel.DataAnnotations.Schema;
using Persistence;

namespace Guild.Domain.Entity;

public class Wiki : BaseEntity<Wiki>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "wiki";

    public string GuildId { get; set; }

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
}
