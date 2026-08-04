namespace Messaging.Domain.Events.Message;

/// <summary>
/// "Go build link previews for this message."
///
/// <para>A separate message from <see cref="MessageCreated"/> rather than work done inside its
/// handler, for three reasons: it is retried independently, so a third-party origin being down
/// costs the send nothing; it is raised from more than one place (a new message, an edit, an
/// unsuppression); and it keeps the outbound-HTTP path off the fan-out that delivers the message,
/// which must stay fast.</para>
/// </summary>
public class UnfurlMessageLinks
{
    public string MessageId { get; set; } = "";

    /// <summary>The message's storage partition key (conversation or channel id). Reading a message
    /// by id alone means a scan in Scylla; every read path in this service carries the context.</summary>
    public string ContextId { get; set; } = "";
}
