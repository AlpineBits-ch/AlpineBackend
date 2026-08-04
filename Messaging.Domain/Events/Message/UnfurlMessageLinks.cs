namespace Messaging.Domain.Events.Message;

/// <summary>"Go build link previews for this message."</summary>
public class UnfurlMessageLinks
{
    public string MessageId { get; set; } = "";

    /// <summary>The message's storage partition key (conversation or channel id).</summary>
    public string ContextId { get; set; } = "";
}
