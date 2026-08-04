namespace Messaging.Contracts.Bus.Commands;

/// <summary>
/// Every replaceable field on this command is a patch, not a snapshot: null means "leave what is
/// stored alone", a value means "replace it".
/// </summary>
public class UpdateMessageCommand
{
    public string MessageId { get; set; }
    public string RequestingAuthorId { get; set; }

    /// <summary>Replacement body.</summary>
    public byte[]? Content { get; set; }

    /// <summary>Replacement embeds.</summary>
    public string? EmbedsJson { get; set; }

    /// <summary>Replacement components.</summary>
    public string? ComponentsJson { get; set; }

    /// <summary>Set when a bot edits its own message through the interaction callback path
    /// (UPDATE_MESSAGE). The ordinary author check compares against the human who sent the
    /// message, which a component-driven edit by definition is not.</summary>
    public bool AllowBotAuthorEdit { get; set; }
}
