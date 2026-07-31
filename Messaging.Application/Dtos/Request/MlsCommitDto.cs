using Facet;
using Messaging.Domain.Entities;

namespace Messaging.Application.Dtos.Request;

/// <summary>
/// Publishes one MLS commit for a group, together with the Welcomes for any devices the commit
/// adds. Both halves land in the same transaction: a commit that added members but whose Welcomes
/// were lost would leave those devices holding a leaf they cannot derive keys for.
/// </summary>
public class PublishMlsCommitDto
{
    /// <summary>Group epoch <i>after</i> applying this commit. Must be exactly the group's current
    /// epoch + 1 - see <see cref="MlsCommit"/> for why the server refuses anything else.</summary>
    public long Epoch { get; set; }

    /// <summary>TLS-serialized MlsMessage carrying the commit.</summary>
    public byte[] Commit { get; set; } = null!;

    /// <summary>Publisher's client device id. Fanout skips it - that device already merged locally.</summary>
    public string SenderDeviceId { get; set; } = null!;

    /// <summary>Which generation this commit was built against. Optional: a client that predates
    /// generations does not send it and is taken to mean the live one, which is the only group it
    /// could have built against. When it <i>is</i> sent and does not match, the commit is refused -
    /// applying a commit built for a replaced group would advance the wrong group and fork everyone.</summary>
    public int? Generation { get; set; }

    /// <summary>Refreshed GroupInfo for external-commit recovery. Optional, but a group whose
    /// stored GroupInfo is older than its retained commits cannot be rejoined by a device that
    /// fell too far behind, so clients should send it on every commit.</summary>
    public byte[]? GroupInfo { get; set; }

    public List<DeviceWelcomeDto> Welcomes { get; set; } = new();
}

/// <summary>Ids of Welcomes whose group the device has actually joined. See
/// <see cref="PendingWelcome"/> for why joining and acknowledging are separate steps.</summary>
public class AckWelcomesDto
{
    public List<string> WelcomeIds { get; set; } = new();
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
}

public class AckWelcomesResultDto
{
    public int Acknowledged { get; set; }
}
