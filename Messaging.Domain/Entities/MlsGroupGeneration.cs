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

    /// <summary>Client device id of the device that built this group, when the caller sent one.</summary>
    public string? ActivatedByDeviceId { get; init; }

    /// <summary>Activation time.</summary>
    public DateTimeOffset ActivatedAt { get; init; }
}

/// <summary>
/// One continuous stretch of end-to-end encryption over a context, backed by exactly one MLS group.
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

    /// <summary>
    /// The one device of <see cref="ActivatedByUserId"/> that actually built this group, or null
    /// when the client sent no <c>X-Device-Id</c>.
    /// </summary>
    public string? ActivatedByDeviceId { get; set; }

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
            ActivatedByDeviceId = string.IsNullOrWhiteSpace(parameters.ActivatedByDeviceId)
                ? null
                : parameters.ActivatedByDeviceId.Trim(),
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
