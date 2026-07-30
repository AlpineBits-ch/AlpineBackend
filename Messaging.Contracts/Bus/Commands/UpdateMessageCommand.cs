namespace Messaging.Contracts.Bus.Commands;

public class UpdateMessageCommand
{
    public string MessageId { get; set; }
    public string RequestingAuthorId { get; set; }
    public byte[] Content { get; set; }
    public string? EmbedsJson { get; set; }

    /// <summary>Replacement components. Null leaves them alone; an empty JSON array clears them,
    /// which is how "disable these buttons now that the flow is finished" is expressed.</summary>
    public string? ComponentsJson { get; set; }

    /// <summary>Set when a bot edits its own message through the interaction callback path
    /// (UPDATE_MESSAGE). The ordinary author check compares against the human who sent the
    /// message, which a component-driven edit by definition is not.</summary>
    public bool AllowBotAuthorEdit { get; set; }
}
