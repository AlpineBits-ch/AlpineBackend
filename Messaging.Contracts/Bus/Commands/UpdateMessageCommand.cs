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

    /// <summary>Replacement flag bitfield (<c>Messaging.Domain.Enums.MessageFlags</c>).</summary>
    public int? Flags { get; set; }

    /// <summary>
    /// Whether this write should count as the author editing the message - i.e. whether clients
    /// should start showing "(edited)".
    /// </summary>
    public bool IsAuthorEdit { get; set; } = true;

    /// <summary>
    /// Set by callers that have already run their own authorization and must not be re-checked
    /// against the author comparison.
    /// </summary>
    public bool AuthorizationAlreadyChecked { get; set; }

    /// <summary>
    /// Generated link previews to merge in, replacing any previously generated ones and leaving
    /// author-written embeds untouched.
    /// </summary>
    public string? GeneratedEmbedsJson { get; set; }

    /// <summary>
    /// Applies the patch only if the stored content still hashes to this (hex SHA-256).
    /// </summary>
    public string? ExpectedContentSha256 { get; set; }
}
