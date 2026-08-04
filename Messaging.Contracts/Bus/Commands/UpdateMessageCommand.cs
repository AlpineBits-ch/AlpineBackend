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

    /// <summary>
    /// Replacement flag bitfield (<c>Messaging.Domain.Enums.MessageFlags</c>). Null leaves the
    /// stored flags alone - the patch rule the rest of this command follows.
    /// </summary>
    public int? Flags { get; set; }

    /// <summary>
    /// Whether this write should count as the author editing the message - i.e. whether clients
    /// should start showing "(edited)".
    ///
    /// <para>False for every write the author did not make: attaching a generated link preview,
    /// suppressing one. Those move <c>UpdatedAt</c> (they really did rewrite the row) but must not
    /// move <c>EditedAt</c>, or posting a URL would mark your own message as edited a second later
    /// with nobody having touched it.</para>
    ///
    /// <para>Defaults to true so that every existing caller - all of which are genuine author
    /// edits - keeps its current behaviour without being changed.</para>
    /// </summary>
    public bool IsAuthorEdit { get; set; } = true;

    /// <summary>
    /// Set by callers that have already run their own authorization and must not be re-checked
    /// against the author comparison.
    ///
    /// <para>Distinct from <see cref="AllowBotAuthorEdit"/> on purpose. That flag means "this bot
    /// owns the message"; this one means "a moderator with DeleteAnyMessage is suppressing a
    /// preview, or the unfurler is attaching one on nobody's behalf". Collapsing them would make
    /// the bot path silently inherit a moderator bypass.</para>
    /// </summary>
    public bool AuthorizationAlreadyChecked { get; set; }

    /// <summary>
    /// Generated link previews to merge in, replacing any previously generated ones and leaving
    /// author-written embeds untouched. Mutually exclusive with <see cref="EmbedsJson"/>, which
    /// replaces the whole array.
    ///
    /// <para>Merging happens here rather than in the caller because the caller would have to read
    /// the message, merge, and write - and an author edit landing in that window would be silently
    /// reverted by the write. The merge has to see the same row the write does.</para>
    /// </summary>
    public string? GeneratedEmbedsJson { get; set; }

    /// <summary>
    /// Applies the patch only if the stored content still hashes to this (hex SHA-256). Null skips
    /// the check.
    ///
    /// <para>The unfurler sets it. Fetching a page takes seconds, and in those seconds the author
    /// can edit the message to point at something else - without this, the preview for the old link
    /// would attach to the new text and there would be no way to tell it was stale.</para>
    /// </summary>
    public string? ExpectedContentSha256 { get; set; }
}
