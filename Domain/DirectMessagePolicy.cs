namespace Domain;

/// <summary>
/// Who may open a direct message with this account.
///
/// <para>Replaces <see cref="DirectMessageSettings"/>, which named the same three-and-a-bit states
/// after the <i>filter</i> rather than after the people admitted - "FilterNonFriends" reads as a
/// spam control and is in fact a contactability policy. The ordering here is deliberately
/// permissive-to-restrictive so that "at least as restrictive as" is a comparison, which
/// T1-11's minor floor needs.</para>
///
/// <para><b>Default is <see cref="Friends"/>, and every failed resolution must land on it or
/// stricter.</b> A policy lookup that cannot reach its data resolves closed - see the cross-cutting
/// rules in docs/specs/privacy.md.</para>
/// </summary>
public enum DirectMessagePolicy
{
    Everyone,
    FriendsAndServerMembers,
    Friends,
    Nobody,
}
