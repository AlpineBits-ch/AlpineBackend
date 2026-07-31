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
}

/// <summary>Ids of Welcomes whose group the device has actually joined.</summary>
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
