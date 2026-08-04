namespace Isle.Api.Services.Privacy;

/// <summary>The T2-19 gate: may this account be registered for positional voice capture?</summary>
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
            // ladder, so reaching here means something below it broke outright.
            logger.LogWarning(e,
                "Could not resolve positional voice consent for {UserId}; refusing capture", userId);
            return false;
        }
    }
}
