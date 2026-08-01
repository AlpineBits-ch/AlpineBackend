using Facet;
using Messaging.Domain.Entities;

namespace Messaging.Application.Dtos.Request;

/// <summary>
/// Publishes one MLS commit for a group, together with the Welcomes for any devices the commit
/// adds.
/// </summary>
public class PublishMlsCommitDto
{
    /// <summary>Group epoch after applying this commit.</summary>
    public long Epoch { get; set; }

    /// <summary>TLS-serialized MlsMessage carrying the commit.</summary>
    public byte[] Commit { get; set; } = null!;

    /// <summary>Publisher's client device id. Fanout skips it - that device already merged locally.</summary>
    public string SenderDeviceId { get; set; } = null!;

    /// <summary>Which generation this commit was built against.</summary>
    public int? Generation { get; set; }

    /// <summary>Refreshed GroupInfo for external-commit recovery.</summary>
    public byte[]? GroupInfo { get; set; }

    public List<DeviceWelcomeDto> Welcomes { get; set; } = new();

    /// <summary>Join requests this commit admits.</summary>
    public List<string> FulfilledJoinRequestIds { get; set; } = new();

    /// <summary>
    /// Set when the payload is a bare proposal (a Remove a leaving device published for the others
    /// to commit) rather than a commit.
    /// </summary>
    public bool IsProposal { get; set; }
}

/// <summary>Ids of Welcomes whose group the device has actually joined.</summary>
public class AckWelcomesDto
{
    public List<string> WelcomeIds { get; set; } = new();

    /// <summary>Required.</summary>
    public string DeviceId { get; set; } = null!;
}

[Facet(typeof(MlsCommit))]
public partial class MlsCommitResponseDto
{
}

/// <summary>Confirms where the group now sits, so the publisher can record the epoch it just
/// established without re-reading the conversation.</summary>
public class MlsCommitPublishedDto
{
    public string ContextId { get; set; } = null!;

    /// <summary>Kept alongside ContextId so clients written against the conversation-only shape
    /// keep reading the field they already read.</summary>
    public string? ConversationId { get; set; }

    public int Generation { get; set; }
    public long Epoch { get; set; }

    /// <summary>Echoes back whether the stored row was a proposal, so a client cannot mistake a
    /// successful proposal publish for the group having moved.</summary>
    public bool IsProposal { get; set; }

    /// <summary>True when the server already held this exact commit from this device and returned
    /// the stored row instead of writing a second one. The publish succeeded - the client should
    /// keep its merged state rather than treating this as a lost race and discarding it.</summary>
    public bool Duplicate { get; set; }
}

public class AckWelcomesResultDto
{
    public int Acknowledged { get; set; }
}
