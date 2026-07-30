using Persistence;

namespace Guild.Domain.Entity;

public class CreateListItemParams
{
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string Text { get; set; } = null!;
    public string? Quantity { get; set; }
    public string? Note { get; set; }
    public string? Section { get; set; }
    public string? AssigneeUserId { get; set; }
    public string AddedByUserId { get; set; } = null!;
    public int Position { get; set; }
}

/// <summary>
/// One line on a <see cref="Enums.ChannelType.List"/> channel - a shopping list entry or a todo,
/// which are the same row with a different client rendering.
/// </summary>
public class ListItem : BaseEntity<ListItem>, IPrefixedEntity
{
    public static string Prefix { get; } = "litm";

    public string ChannelId { get; set; } = null!;

    /// <summary>Denormalized from the parent channel so authorization and audit logging don't need
    /// a join back to Channel on every item write - same trade-off as ForumTag.GuildId.</summary>
    public string GuildId { get; set; } = null!;

    public string Text { get; set; } = null!;

    /// <summary>Free text ("2", "2 packs", "a bunch") rather than a number+unit pair.</summary>
    public string? Quantity { get; set; }

    public string? Note { get; set; }

    /// <summary>Free-text grouping ("Dairy", "Hardware store").</summary>
    public string? Section { get; set; }

    public string? AssigneeUserId { get; set; }
    public string AddedByUserId { get; set; } = null!;

    public bool IsChecked { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    public string? CheckedByUserId { get; set; }

    public int Position { get; set; }

    /// <summary>Set when the item was appended automatically because a pantry item fell to its
    /// low-stock threshold. Lets the client badge it ("added by the pantry") and lets
    /// PantryItem.RestockedAt be reconciled if the item is deleted off the list.</summary>
    public string? SourcePantryItemId { get; set; }

    public static ListItem Create(CreateListItemParams @params)
    {
        var date = DateTimeOffset.UtcNow;
        return new ListItem
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            ChannelId = @params.ChannelId,
            GuildId = @params.GuildId,
            Text = @params.Text,
            Quantity = @params.Quantity,
            Note = @params.Note,
            Section = @params.Section,
            AssigneeUserId = @params.AssigneeUserId,
            AddedByUserId = @params.AddedByUserId,
            Position = @params.Position,
        };
    }

    public void Check(string userId)
    {
        IsChecked = true;
        CheckedAt = DateTimeOffset.UtcNow;
        CheckedByUserId = userId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Uncheck()
    {
        IsChecked = false;
        CheckedAt = null;
        CheckedByUserId = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
