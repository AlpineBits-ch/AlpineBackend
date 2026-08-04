namespace Isle.Api.Services.Privacy;

/// <summary>
/// The T2-19 gate: may this account be registered for <i>positional</i> voice capture?
///
/// <para>Isle's proximity voice is the one place in the product where a user's microphone is tied
/// to their location in a world other people share. <c>AllowPositionalVoiceCapture</c> is the
/// control over that, and until this type existed it was a stored flag that gated nothing - the
/// exact failure T0-4 of the privacy spec calls out.</para>
///
/// <para>What "not registered for positional capture" means concretely here: no entry in
/// <c>VoicePlayerRegistry</c>. That single mapping is the whole trigger for the positional
/// pipeline - <c>StatsStreamIngestionService</c> ignores position snapshots for anyone it does not
/// resolve, so no cluster membership, no peer audibility, no SFU position broadcast and no
/// published audio track follow from it. Refusing there is therefore the narrowest place that
/// actually stops capture rather than merely hiding it.</para>
///
/// <para>Refusing Isle voice is not refusing voice: a user with this off can still speak in Guild
/// and Messaging voice, which are membership-scoped rather than location-scoped. Isle has no
/// non-positional channel of its own, so here the two are the same refusal.</para>
///
/// <para><b>Fails closed.</b> An unresolvable user id, an unreadable cache and an unreachable
/// Identity all answer <c>false</c>. The cost of a false refusal is a player who has to rejoin
/// voice; the cost of a false grant is capturing someone who said no.</para>
/// </summary>
public sealed class PositionalVoiceConsent(
    PrivacySettingsCache settings,
    ILogger<PositionalVoiceConsent> logger)
{
    /// <summary>
    /// True only when the account has positively been found to allow positional capture.
    /// </summary>
    public async Task<bool> MayCaptureAsync(string? userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;

        try
        {
            var record = await settings.GetAsync(userId, ct);
            if (!record.AllowPositionalVoiceCapture)
                logger.LogDebug("Positional voice capture refused for {UserId}: consent is off", userId);

            return record.AllowPositionalVoiceCapture;
        }
        catch (Exception e)
        {
            // PrivacySettingsCache already absorbs a failed Identity lookup into its own fallback
            // ladder, so reaching here means something below it broke outright. Same answer either
            // way: unresolved is refused.
            logger.LogWarning(e,
                "Could not resolve positional voice consent for {UserId}; refusing capture", userId);
            return false;
        }
    }
}
