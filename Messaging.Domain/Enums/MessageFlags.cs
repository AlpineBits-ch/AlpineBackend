namespace Messaging.Domain.Enums;

/// <summary>Discord-compatible message flag bitfield, stored on <c>Message.Flags</c>.</summary>
public static class MessageFlags
{
    public const int None = 0;

    /// <summary>1 &lt;&lt; 0 CROSSPOSTED - not implemented.</summary>
    /// <summary>1 &lt;&lt; 1 IS_CROSSPOST - not implemented.</summary>

    /// <summary>
    /// The message's link previews are hidden, and no new ones may be generated for it.
    /// </summary>
    public const int SuppressEmbeds = 1 << 2;

    /// <summary>1 &lt;&lt; 3 SOURCE_MESSAGE_DELETED - not implemented.</summary>
    /// <summary>1 &lt;&lt; 4 URGENT - not implemented.</summary>
    /// <summary>1 &lt;&lt; 5 HAS_THREAD - not implemented (threads are modelled relationally).</summary>
    /// <summary>1 &lt;&lt; 6 EPHEMERAL - not implemented.</summary>
    /// <summary>1 &lt;&lt; 7 LOADING - not implemented.</summary>

    /// <summary>1 &lt;&lt; 12 SUPPRESS_NOTIFICATIONS ("@silent").</summary>
    public const int SuppressNotifications = 1 << 12;

    public static bool Has(int flags, int flag) => (flags & flag) == flag;

    public static int With(int flags, int flag) => flags | flag;

    public static int Without(int flags, int flag) => flags & ~flag;
}
