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
///
/// <para><b>Epoch is the ordering and the dedup key.</b> <see cref="Epoch"/> is the epoch the group
/// is in <i>after</i> this commit is applied, and (<see cref="ContextId"/>, <see cref="Generation"/>,
/// <see cref="Epoch"/>) is unique. Two members who commit concurrently therefore race for the same
/// row: exactly one wins, the loser gets a conflict and has to re-fetch, re-apply and re-issue its
/// change. That is the whole fork-resolution story - the server never merges commits, it only
/// refuses the second one.</para>
///
/// <para><see cref="Generation"/> is part of that key rather than an afterthought: encryption can be
/// switched off and back on, and the replacement group starts counting epochs from zero again. Keyed
/// on epoch alone, the new group's first commit would collide with the old group's.</para>
///
/// <para>Context columns mirror <see cref="Message"/>: <see cref="ContextId"/> is the group key and
/// carries either a conversation id or a channel id, with the matching typed column set for
/// cascade-delete and querying. Guild-channel MLS therefore needs no schema change - only an
/// authorization resolver that can answer "is this user in that channel's group".</para>
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

    /// <summary>Group epoch <i>after</i> this commit is applied. Unique per (context, generation).</summary>
    public long Epoch { get; set; }

    /// <summary>Base64/TLS-serialized MlsMessage carrying the commit.</summary>
    public byte[] Commit { get; set; } = null!;

    public string SenderUserId { get; set; } = null!;

    /// <summary>Client device id of the publisher, so fanout can skip the device that already
    /// merged this commit locally.</summary>
    public string SenderDeviceId { get; set; } = null!;

    /// <summary>
    /// True when this row carries a bare <i>proposal</i> rather than a commit.
    ///
    /// <para><b>A proposal does not advance an epoch.</b> Processing one changes no client's MLS
    /// epoch, so it neither claims an epoch slot nor moves the generation's counter - the unique
    /// index above is filtered to exclude these rows precisely so a proposal announced at epoch N+1
    /// does not block the real commit that establishes N+1.</para>
    ///
    /// <para>It travels this table anyway because it still has to reach every device, in order,
    /// exactly once. Clients must not count it toward "commits applied" when deciding whether to
    /// keep paging: the server used to advance the epoch for these, and the resulting catch-up loop
    /// re-fetched the same proposal forever.</para>
    /// </summary>
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
