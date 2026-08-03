namespace Messaging.Contracts.Bus.Request;

/// <summary>
/// A page of the caller's mention index - direct and @here mentions, newest first.
///
/// <para>@everyone and @role are not here and never will be: they are one row each on the Guild
/// side, evaluated against membership at read time. Guild merges the two sources.</para>
/// </summary>
public class GetUserMentionsRequest
{
    public required string UserId { get; init; }

    /// <summary>Exclusive upper bound - the clustering key of the last row of the previous page.
    /// Paired with <see cref="BeforeMessageId"/>, since two mentions can share a millisecond.</summary>
    public DateTimeOffset? Before { get; init; }

    public string? BeforeMessageId { get; init; }

    /// <summary>Lower bound on age. Clamped to the index's retention window - asking for older than
    /// that returns nothing rather than pretending the rows are still there.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Restricts to one guild. Null means every guild.</summary>
    public string? GuildId { get; init; }

    /// <summary>When false, DM mentions are left out. Guild mentions carry a GuildId; DMs do not,
    /// which is the whole filter.</summary>
    public bool IncludeDms { get; init; } = true;

    public int Limit { get; init; } = 25;
}

/// <summary>Removes one row from the index - the X on a mention, and the reap of a row whose message
/// has since been deleted.</summary>
public class DeleteUserMentionRequest
{
    public required string UserId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string MessageId { get; init; }
}

public class DeleteUserMentionResponse
{
    public bool Deleted { get; init; }
}
