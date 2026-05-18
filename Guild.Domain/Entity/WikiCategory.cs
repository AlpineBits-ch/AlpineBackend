using System.ComponentModel.DataAnnotations.Schema;
using Domain;
using Guild.Domain.Events.Wiki;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateWikiCategoryParams
{
    public string GuildId { get; init; }
    public string Name { get; init; }
    public int Position { get; init; } = 0;
    public string? ParentCategoryId { get; init; }
}

public class WikiCategory : Aggregate<WikiCategory>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "wkca";

    public string GuildId { get; set; }
    public string Name { get; set; }
    public int Position { get; set; }
    public string? ParentCategoryId { get; set; }

    public static WikiCategory Create(CreateWikiCategoryParams @params)
    {
        var id = GenerateId();
        var date = DateTime.UtcNow;
        var category = new WikiCategory
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,
            GuildId = @params.GuildId,
            Name = @params.Name,
            Position = @params.Position,
            ParentCategoryId = @params.ParentCategoryId,
        };
        category.AddDomainEvent(new WikiCategoryCreated { CategoryId = id, GuildId = @params.GuildId, ParentCategoryId = @params.ParentCategoryId });
        return category;
    }

    public void RaiseUpdated() =>
        AddDomainEvent(new WikiCategoryUpdated { CategoryId = Id, GuildId = GuildId, ParentCategoryId = ParentCategoryId });

    public void RaiseDeleted() =>
        AddDomainEvent(new WikiCategoryDeleted { CategoryId = Id, GuildId = GuildId });
}
