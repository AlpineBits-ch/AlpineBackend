namespace Guild.Contracts.Bus.Events;

/// <summary>A phone notification for something that happened in a household module.</summary>
public class HouseholdPushRequested
{
    public string GuildId { get; set; } = null!;

    /// <summary>The module channel this came from, so tapping the notification opens the right
    /// board. Null for the guild-scoped modules, which have no channel.</summary>
    public string? ChannelId { get; set; }

    /// <summary>Who to notify.</summary>
    public List<string> UserIds { get; set; } = [];

    /// <summary>Which module produced this, as a stable slug the client routes on:
    /// <c>chore.due</c>, <c>ledger.expense</c>, <c>decision.opened</c>, <c>pantry.expiring</c>.
    /// A client that does not recognise the kind still has a title and a body to render.</summary>
    public string Kind { get; set; } = null!;

    /// <summary>The row the notification is about - an occurrence id, an expense id.</summary>
    public string? TargetId { get; set; }

    /// <summary>Notification title.</summary>
    public string Title { get; set; } = null!;

    public string Body { get; set; } = "";
}
