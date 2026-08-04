namespace Messaging.Contracts.Bus.Commands;

/// <summary>
/// Every replaceable field on this command is a patch, not a snapshot: null means "leave what is
/// stored alone", a value means "replace it". An edit that only carries new text therefore has to
/// leave <see cref="EmbedsJson"/> and <see cref="ComponentsJson"/> null - sending null to mean
/// "the caller had nothing to say about embeds" is exactly what wiped them before.
/// </summary>
public class UpdateMessageCommand
{
    public string MessageId { get; set; }
    public string RequestingAuthorId { get; set; }

    /// <summary>Replacement body. Null leaves the stored content alone - which is what an
    /// embeds-only or components-only edit sends.</summary>
    public byte[]? Content { get; set; }

    /// <summary>Replacement embeds. Null leaves them alone; an empty JSON array clears them.
    /// Same rule as <see cref="ComponentsJson"/> - see the type-level note.</summary>
    public string? EmbedsJson { get; set; }

    /// <summary>Replacement components. Null leaves them alone; an empty JSON array clears them,
    /// which is how "disable these buttons now that the flow is finished" is expressed.</summary>
    public string? ComponentsJson { get; set; }

    /// <summary>Set when a bot edits its own message through the interaction callback path
    /// (UPDATE_MESSAGE). The ordinary author check compares against the human who sent the
    /// message, which a component-driven edit by definition is not.</summary>
    public bool AllowBotAuthorEdit { get; set; }
}
