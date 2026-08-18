namespace Messaging.Application.Dtos.Request;

/// <summary>A draft as the client last had it.</summary>
public class UpsertMessageDraftDto
{
    /// <summary>The body as typed. Empty is not a draft and deletes the stored one.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>The message being replied to, or null.</summary>
    public string? InReplyTo { get; set; }

    /// <summary>
    /// Files already uploaded for this draft. A draft carrying only attachments is still a draft:
    /// the upload is the part that cost the author something, so an empty body no longer means
    /// there is nothing worth keeping.
    /// </summary>
    public List<string> Attachments { get; set; } = [];

    /// <summary>
    /// Which of the caller's devices wrote this, echoed back on the realtime event so that device
    /// can ignore its own write rather than applying it over text still being typed. Same id the
    /// send path carries under this name.
    /// </summary>
    public string? SenderDeviceId { get; set; }
}
