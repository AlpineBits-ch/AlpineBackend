namespace Billing.Application.Promotions;

/// <summary>
/// What the promotion machinery needs configuring, which is one secret and two numbers.
/// </summary>
public sealed class PromotionOptions
{
    public const string SectionName = "Billing:Promotions";

    /// <summary>The environment variable the salt comes from.</summary>
    public const string SaltVariable = "PROMOTION_HASH_SALT";

    /// <summary>
    /// Short enough not to be a nuisance and long enough that the salt is not guessable.
    /// </summary>
    public const int MinimumSaltLength = 16;

    /// <summary>The HMAC key every identity hash is computed under.</summary>
    public string HashSalt { get; set; } =
        Environment.GetEnvironmentVariable(SaltVariable)?.Trim() ?? string.Empty;

    /// <summary>How long a promotion endpoint waits on Identity before treating the silence as an
    /// outage. Short, because the caller is a person looking at a button and the failure is a
    /// refusal.</summary>
    public int IdentityTimeoutSeconds { get; set; } = 10;

    public TimeSpan IdentityTimeout => TimeSpan.FromSeconds(Math.Clamp(IdentityTimeoutSeconds, 1, 60));

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(HashSalt) && HashSalt.Trim().Length >= MinimumSaltLength;

    /// <summary>Refuses to start without a salt, rather than falling back to one.</summary>
    public void EnsureConfigured()
    {
        if (IsConfigured) return;

        throw new InvalidOperationException(
            $"{SaltVariable} is unset or shorter than {MinimumSaltLength} characters. Promotions hash "
            + "the phone number, the device ids and the card fingerprint they match repeat redemptions "
            + "on, and there is deliberately no default salt: a compiled-in one would make every hash "
            + "on this instance computable by anybody with the source, which turns the anti-abuse "
            + "table into a lookup service for whether a given number has an account here. Set it to a "
            + "long random string and keep it - changing it makes every existing mark unmatchable.");
    }
}
