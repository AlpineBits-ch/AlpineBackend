namespace Echo.Voice.Abuse;

/// <summary>
/// A publisher is sending video taller than their plan allows, as measured at the SFU.
/// </summary>
/// <param name="UserId">Who is publishing it.</param>
/// <param name="GuildId">
/// Null for a direct call, which has no guild plan behind it - so an over-publish there is against
/// the operator's ceiling or the user's own plan.
/// </param>
/// <param name="RoomKind">See <c>VoiceRoomKind</c>.</param>
/// <param name="TrackName">Which publication.</param>
/// <param name="ObservedHeight">What the SFU says is being sent.</param>
/// <param name="DeclaredHeight">
/// What the publisher said they would send, or zero if they never said.
/// </param>
/// <param name="GrantedRung">
/// The rung they were entitled to, so a consumer does not have to resolve entitlements again to
/// know how far over this is.
/// </param>
public sealed record VoiceOverPublishDetected(
    string UserId,
    string? GuildId,
    string RoomKind,
    string RoomId,
    string TrackName,
    int ObservedHeight,
    int DeclaredHeight,
    string GrantedRung,
    DateTimeOffset DetectedAt)
{
    /// <summary>How far past the rung they are, as a multiple of the permitted height.</summary>
    public double Overshoot(int permittedHeight) =>
        permittedHeight <= 0 ? 0 : (double)ObservedHeight / permittedHeight;

    /// <summary>Whether the publisher's own declaration was honest, whatever their plan says. False
    /// means they told us one size and are sending a larger one, which is the only shape here that
    /// no misconfiguration explains on its own.</summary>
    public bool DeclarationMatchesReality =>
        DeclaredHeight <= 0 || ObservedHeight <= DeclaredHeight;
}
