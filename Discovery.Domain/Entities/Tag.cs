using Persistence;

namespace Discovery.Domain.Entities;

public class Tag : BaseEntity<Tag>, IPrefixedEntity
{
    public static string Prefix { get; } = "tag";

    public string Slug { get; set; } = null!;
    public string DisplayName { get; set; } = null!;

    /// <summary>Set when staff merge this tag into another. Reads resolve through it.</summary>
    public string? AliasOf { get; set; }

    public int UsageCount { get; set; }
}
