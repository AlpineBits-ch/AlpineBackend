namespace Messaging.Domain.Enums;

/// <summary>
/// Discord-compatible message flag bitfield, stored on <c>Message.Flags</c>.
///
/// <para>Values match Discord's so a Discord-compat client reading our messages through the Bots
/// gateway gets the semantics it expects without a translation table. Bits we do not implement are
/// listed anyway - the gaps are load-bearing documentation, and picking an unused bit later without
/// this list would silently collide with a real Discord flag the moment a client checked it.</para>
/// </summary>
public static class MessageFlags
{
    public const int None = 0;

    /// <summary>1 &lt;&lt; 0 CROSSPOSTED - not implemented.</summary>
    /// <summary>1 &lt;&lt; 1 IS_CROSSPOST - not implemented.</summary>

    /// <summary>
    /// The message's link previews are hidden, and no new ones may be generated for it.
    ///
    /// <para>This is <b>message</b> state, not per-viewer state - which is the whole point. When
    /// the author dismisses a preview it is gone for everyone who can see the message, because the
    /// bit lives on the row every viewer reads.</para>
    ///
    /// <para>It is also why suppression cannot simply be "write an empty embeds array": the unfurl
    /// job retries on failure and re-runs when a message is edited, so without a persistent record
    /// that a human said no, a dismissed preview reappears.</para>
    /// </summary>
    public const int SuppressEmbeds = 1 << 2;

    /// <summary>1 &lt;&lt; 3 SOURCE_MESSAGE_DELETED - not implemented.</summary>
    /// <summary>1 &lt;&lt; 4 URGENT - not implemented.</summary>
    /// <summary>1 &lt;&lt; 5 HAS_THREAD - not implemented (threads are modelled relationally).</summary>
    /// <summary>1 &lt;&lt; 6 EPHEMERAL - not implemented.</summary>
    /// <summary>1 &lt;&lt; 7 LOADING - not implemented.</summary>

    /// <summary>
    /// 1 &lt;&lt; 12 SUPPRESS_NOTIFICATIONS ("@silent"). Not implemented; reserved so nothing
    /// else claims it.
    /// </summary>
    public const int SuppressNotifications = 1 << 12;

    public static bool Has(int flags, int flag) => (flags & flag) == flag;

    public static int With(int flags, int flag) => flags | flag;

    public static int Without(int flags, int flag) => flags & ~flag;
}
