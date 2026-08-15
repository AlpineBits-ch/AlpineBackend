namespace Billing.Domain.Promotions;

/// <summary>The conditions a campaign can require of the account redeeming it.</summary>
[Flags]
public enum PromotionEligibility
{
    None = 0,

    /// <summary><c>EmailVerifiedAt</c> is set.</summary>
    VerifiedEmail = 1,

    /// <summary>A phone number has been entered.</summary>
    PhoneNumberOnFile = 2,

    /// <summary>The account is older than the campaign's <c>MinimumAccountAgeDays</c>.</summary>
    MinimumAccountAge = 4,

    /// <summary>At least one active device.</summary>
    RegisteredDevice = 8,

    /// <summary>The account has never paid for a subscription here.</summary>
    NoPriorSubscription = 16,

    /// <summary>A card is on file with Stripe.</summary>
    PaymentCard = 32,
}

/// <summary>
/// What the platform knows about one account at the moment a redemption is evaluated.
/// </summary>
/// <param name="EmailVerified">From Identity's <c>EmailVerifiedAt</c>.</param>
/// <param name="PhoneNumberOnFile">Whether a number has been entered.</param>
/// <param name="AccountCreatedAt">
/// Null when Identity could not answer, which fails the age rule rather than passing it.
/// </param>
/// <param name="DeviceCount">Active devices, from the consolidated device set.</param>
/// <param name="HasPriorSubscription">Billing's own answer, not Identity's.</param>
/// <param name="HasPaymentCard">Whether Stripe holds a card for this account.</param>
public sealed record PromotionSignals(
    bool EmailVerified,
    bool PhoneNumberOnFile,
    DateTimeOffset? AccountCreatedAt,
    int DeviceCount,
    bool HasPriorSubscription,
    bool HasPaymentCard);

/// <summary>The two things a rule needs beyond the signals: what time it is, and the one threshold a
/// campaign gets to choose.</summary>
public sealed record PromotionEligibilityContext(DateTimeOffset Now, int MinimumAccountAgeDays);

/// <summary>One rule, as data.</summary>
public sealed record PromotionEligibilityRule(
    PromotionEligibility Signal,
    string Code,
    Func<PromotionSignals, PromotionEligibilityContext, bool> IsSatisfied,
    string Refusal);

/// <summary>
/// The single source of truth for "which signal means which check", in the spirit of
/// <c>GuildFeatureMap</c>: one table states it, one service consults it, and no endpoint has to
/// remember.
/// </summary>
public static class PromotionEligibilityMap
{
    private static readonly PromotionEligibilityRule[] Rules =
    [
        new(PromotionEligibility.VerifiedEmail,
            "verified_email",
            (signals, _) => signals.EmailVerified,
            "This offer needs a confirmed email address. Confirm the one on the account and try again."),

        new(PromotionEligibility.PhoneNumberOnFile,
            "phone_number_on_file",
            (signals, _) => signals.PhoneNumberOnFile,
            "This offer needs a phone number on the account."),

        new(PromotionEligibility.MinimumAccountAge,
            "minimum_account_age",
            (signals, context) => signals.AccountCreatedAt is { } created
                                  && created <= context.Now.AddDays(-context.MinimumAccountAgeDays),
            "This offer is not open to brand new accounts."),

        new(PromotionEligibility.RegisteredDevice,
            "registered_device",
            (signals, _) => signals.DeviceCount > 0,
            "This offer needs at least one device signed in to the account."),

        new(PromotionEligibility.NoPriorSubscription,
            "no_prior_subscription",
            (signals, _) => !signals.HasPriorSubscription,
            "This offer is for accounts that have never subscribed."),

        new(PromotionEligibility.PaymentCard,
            "payment_card",
            (signals, _) => signals.HasPaymentCard,
            "This offer needs a card on the account. It is not charged during the trial."),
    ];

    /// <summary>Every rule there is, for the console and for the test that asserts the enum and the
    /// table cannot drift apart.</summary>
    public static IReadOnlyList<PromotionEligibilityRule> All => Rules;

    /// <summary>The rules a campaign requires that this account does not satisfy.</summary>
    public static IReadOnlyList<PromotionEligibilityRule> Unsatisfied(
        PromotionEligibility required,
        PromotionSignals signals,
        PromotionEligibilityContext context)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(context);

        var failed = new List<PromotionEligibilityRule>();

        foreach (var rule in Rules)
        {
            if (!required.HasFlag(rule.Signal)) continue;
            if (!rule.IsSatisfied(signals, context)) failed.Add(rule);
        }

        return failed;
    }

    /// <summary>The signals a mask carries, by name.</summary>
    public static IReadOnlyList<string> Names(PromotionEligibility mask)
    {
        var names = new List<string>();

        foreach (var rule in Rules)
        {
            if (mask.HasFlag(rule.Signal)) names.Add(rule.Code);
        }

        return names;
    }

    /// <summary>Parses a list of rule codes back into a mask, ignoring nothing: an unknown code is an
    /// operator asking for a control that does not exist, and silently opening the campaign wider than
    /// they asked is the one outcome a campaign editor must not have.</summary>
    public static PromotionEligibility Parse(IEnumerable<string>? codes)
    {
        if (codes is null) return PromotionEligibility.None;

        var mask = PromotionEligibility.None;

        foreach (var code in codes)
        {
            if (string.IsNullOrWhiteSpace(code)) continue;

            var trimmed = code.Trim();

            var rule = Rules.FirstOrDefault(
                candidate => string.Equals(candidate.Code, trimmed, StringComparison.OrdinalIgnoreCase));

            if (rule is null)
            {
                throw new ArgumentException(
                    $"'{trimmed}' is not an eligibility rule. Known rules: "
                    + $"{string.Join(", ", Rules.Select(known => known.Code))}.",
                    nameof(codes));
            }

            mask |= rule.Signal;
        }

        return mask;
    }
}
