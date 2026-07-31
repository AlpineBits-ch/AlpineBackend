using Messaging.Domain.Enums;
using Persistence;

namespace Messaging.Domain.Entities;

public class CreateMlsGroupGenerationParams
{
    public string ContextId { get; init; } = null!;
    public string? ConversationId { get; init; }
    public string? ChannelId { get; init; }
    public int Generation { get; init; }
    public byte[] MlsGroupId { get; init; } = null!;
    public byte[]? MlsGroupInfo { get; init; }
    public long Epoch { get; init; }
    public string ActivatedByUserId { get; init; } = null!;

    /// <summary>Activation time. Passed in rather than read off the clock here because the toggle
    /// cooldown is measured against it, and a generation that stamped itself from
    /// <c>DateTimeOffset.UtcNow</c> while the caller reasoned about a different instant would make
    /// that window silently wrong.</summary>
    public DateTimeOffset ActivatedAt { get; init; }
}

/// <summary>
/// One continuous stretch of end-to-end encryption over a context, backed by exactly one MLS group.
///
/// <para><b>Why generations exist at all.</b> Encryption can be switched off and back on. The second
/// group is a genuinely new group - new key material, a roster resolved at that moment, and an epoch
/// counter that starts again from zero. It is not the old group resumed, and it must not be: reusing
/// the old group would hand its history to whoever joined in the meantime. So every enable mints the
/// next <see cref="Generation"/>, and every artifact that belongs to a group - commits, Welcomes,
/// messages - carries that number. Without it, "epoch 3" is ambiguous the moment a context has been
/// toggled twice, and a client would decrypt against the wrong group's keys.</para>
///
/// <para><b>What toggling off does not do.</b> Terminating a generation does not decrypt anything.
/// The messages sent under it stay ciphertext forever, readable only by devices that still hold that
/// group's state. That is the honest consequence of end-to-end encryption and is surfaced to the
/// caller on disable rather than hidden - see the terminated-message count in the disable response.</para>
/// </summary>
public class MlsGroupGeneration : BaseEntity<MlsGroupGeneration>, IPrefixedEntity
{
    public static string Prefix { get; } = "mlsg";

    /// <summary>Conversation id or channel id - the context this group encrypts.</summary>
    public string ContextId { get; set; } = null!;

    /// <summary>Set when the context is a conversation; drives cascade delete.</summary>
    public string? ConversationId { get; set; }

    /// <summary>Set when the context is a guild channel. No FK - channels live in the Guild service.</summary>
    public string? ChannelId { get; set; }

    /// <summary>1-based, monotonic per context. Never reused, including after termination.</summary>
    public int Generation { get; set; }

    public byte[] MlsGroupId { get; set; } = null!;

    /// <summary>Latest GroupInfo, for rejoining by external commit. Refreshed on every commit.</summary>
    public byte[]? MlsGroupInfo { get; set; }

    /// <summary>Current epoch of this group.</summary>
    public long Epoch { get; set; }

    public MlsGenerationState State { get; set; } = MlsGenerationState.Active;

    public DateTimeOffset ActivatedAt { get; set; }
    public string ActivatedByUserId { get; set; } = null!;

    public DateTimeOffset? TerminatedAt { get; set; }
    public string? TerminatedByUserId { get; set; }

    public bool IsActive => State == MlsGenerationState.Active;

    public static MlsGroupGeneration Create(CreateMlsGroupGenerationParams parameters)
    {
        var date = parameters.ActivatedAt == default ? DateTimeOffset.UtcNow : parameters.ActivatedAt;
        return new MlsGroupGeneration
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            ContextId = parameters.ContextId,
            ConversationId = parameters.ConversationId,
            ChannelId = parameters.ChannelId,
            Generation = parameters.Generation,
            MlsGroupId = parameters.MlsGroupId,
            MlsGroupInfo = parameters.MlsGroupInfo,
            Epoch = parameters.Epoch,
            State = MlsGenerationState.Active,
            ActivatedAt = date,
            ActivatedByUserId = parameters.ActivatedByUserId,
        };
    }

    public void Terminate(string userId, DateTimeOffset now)
    {
        State = MlsGenerationState.Terminated;
        TerminatedAt = now;
        TerminatedByUserId = userId;
        UpdatedAt = now;
    }
}
