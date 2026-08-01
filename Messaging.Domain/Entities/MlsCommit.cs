using Messaging.Domain.Aggregates;
using Persistence;

namespace Messaging.Domain.Entities;

public class CreateMlsCommitParams
{
    public string ContextId { get; init; } = null!;
    public string? ConversationId { get; init; }
    public string? ChannelId { get; init; }
    public int Generation { get; init; }
    public long Epoch { get; init; }
    public byte[] Commit { get; init; } = null!;
    public string SenderUserId { get; init; } = null!;
    public string SenderDeviceId { get; init; } = null!;
    public bool IsProposal { get; init; }
}

/// <summary>
/// One TLS-serialized MLS commit (or proposal-bearing commit) for a group, kept so that a device
/// which was offline when it was published can catch up rather than being permanently forked off
/// the group.
/// </summary>
public class MlsCommit : BaseEntity<MlsCommit>, IPrefixedEntity
{
    public static string Prefix { get; } = "mlsc";

    /// <summary>Conversation id or channel id - the MLS group this commit belongs to.</summary>
    public string ContextId { get; set; } = null!;

    /// <summary>Set when the group is a conversation; drives cascade delete.</summary>
    public string? ConversationId { get; set; }

    /// <summary>Set when the group is a guild channel. No FK - channels live in the Guild service.</summary>
    public string? ChannelId { get; set; }

    /// <summary>Which <see cref="MlsGroupGeneration"/> of this context the commit belongs to.</summary>
    public int Generation { get; set; }

    /// <summary>Group epoch after this commit is applied. Unique per (context, generation).</summary>
    public long Epoch { get; set; }

    /// <summary>Base64/TLS-serialized MlsMessage carrying the commit.</summary>
    public byte[] Commit { get; set; } = null!;

    public string SenderUserId { get; set; } = null!;

    /// <summary>Client device id of the publisher, so fanout can skip the device that already
    /// merged this commit locally.</summary>
    public string SenderDeviceId { get; set; } = null!;

    /// <summary>True when this row carries a bare proposal rather than a commit.</summary>
    public bool IsProposal { get; set; }

    public static MlsCommit Create(CreateMlsCommitParams parameters)
    {
        var date = DateTimeOffset.UtcNow;
        return new MlsCommit
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            ContextId = parameters.ContextId,
            ConversationId = parameters.ConversationId,
            ChannelId = parameters.ChannelId,
            Generation = parameters.Generation,
            Epoch = parameters.Epoch,
            Commit = parameters.Commit,
            SenderUserId = parameters.SenderUserId,
            SenderDeviceId = parameters.SenderDeviceId,
            IsProposal = parameters.IsProposal,
        };
    }
}
