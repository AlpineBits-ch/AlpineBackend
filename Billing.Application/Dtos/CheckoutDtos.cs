using System.Text.Json;
using System.Text.Json.Serialization;
using Billing.Domain.Aggregates;
using Echo.Entitlements.Model;
using Echo.Entitlements.Wire;

namespace Billing.Application.Dtos;

/// <summary>
/// The machine-readable refusals the checkout surface emits, and the whole set of them.
/// </summary>
public static class BillingErrorCodes
{
    /// <summary>404. Stripe is not configured on this instance, so nothing is for sale.</summary>
    public const string BillingDisabled = "billing_disabled";

    /// <summary>400. The plan exists and is not sold - no price, or no Stripe price behind it.</summary>
    public const string NotPurchasable = "not_purchasable";

    /// <summary>400. No plan by that name.</summary>
    public const string UnknownPlan = "unknown_plan";

    /// <summary>404. No subscription by that id that this caller may see.</summary>
    public const string UnknownSubscription = "unknown_subscription";

    /// <summary>404. No card with that id on this account.</summary>
    public const string UnknownPaymentMethod = "unknown_payment_method";

    /// <summary>409. This subject already has a live subscription. Offer "change plan" instead.</summary>
    public const string AlreadySubscribed = "already_subscribed";

    /// <summary>403. The caller lacks ManageGuild on the subject.</summary>
    public const string NotPermitted = "not_permitted";

    /// <summary>403. They manage the guild, but somebody else's card is behind it.</summary>
    public const string NotThePayer = "not_the_payer";

    /// <summary>409. Resume was called on a subscription that has already ended.</summary>
    public const string SubscriptionLapsed = "subscription_lapsed";

    /// <summary>409. Detaching the only card under a live subscription.</summary>
    public const string LastPaymentMethod = "last_payment_method";

    /// <summary>502. Stripe refused or could not be reached. The message is safe to display.</summary>
    public const string StripeError = "stripe_error";
}

/// <summary>
/// Writes <see cref="SubjectKind"/> as <c>guild</c> or <c>user</c>, and reads either casing back.
/// </summary>
public sealed class LowercaseSubjectKindConverter : JsonConverter<SubjectKind>
{
    public override SubjectKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

        if (Enum.TryParse<SubjectKind>(text, ignoreCase: true, out var kind)) return kind;

        throw new JsonException(
            $"'{text}' is not a subject kind. Expected '{EntitlementSubjectKinds.Guild}' or "
            + $"'{EntitlementSubjectKinds.User}'.");
    }

    public override void Write(Utf8JsonWriter writer, SubjectKind value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(EntitlementSubjectKinds.Of(value));
    }
}

/// <summary>
/// <see cref="SubscriptionStatus"/> back out as the Stripe string it was parsed from.
/// </summary>
public static class SubscriptionStatusWire
{
    public static string Of(SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.Incomplete => "incomplete",
        SubscriptionStatus.IncompleteExpired => "incomplete_expired",
        SubscriptionStatus.Trialing => "trialing",
        SubscriptionStatus.Active => "active",
        SubscriptionStatus.PastDue => "past_due",
        SubscriptionStatus.Canceled => "canceled",
        SubscriptionStatus.Unpaid => "unpaid",
        SubscriptionStatus.Paused => "paused",

        // Stripe sent something this build has never heard of, and the string it sent is already
        // lost by the time a row is read back.
        _ => "unknown",
    };
}

/// <summary>A billing address, collected by the Address Element on the same screen as the card.
/// Optional: automatic tax needs it, and a purchase is not refused for the want of it.</summary>
public sealed record BillingAddressDto(
    string Country,
    string? Line1 = null,
    string? Line2 = null,
    string? City = null,
    string? State = null,
    string? PostalCode = null);

/// <summary>What the client posts to start a subscription.</summary>
public sealed record CreateSubscriptionRequest(
    string PlanName,
    [property: JsonConverter(typeof(LowercaseSubjectKindConverter))] SubjectKind SubjectKind,
    string SubjectId,
    BillingAddressDto? Address = null,
    string? Email = null);

/// <summary>The body of both preview-change and change.</summary>
public sealed record ChangePlanRequest(string PlanName);

/// <summary>One sellable plan on the customer-facing catalogue.</summary>
public sealed record CataloguePlanDto(
    string Name,
    string DisplayName,
    string? Description,
    int VersionNumber,
    [property: JsonConverter(typeof(LowercaseSubjectKindConverter))] SubjectKind SubjectKind,
    long? PriceMinorUnits,
    string? Currency,
    string Interval,
    bool Purchasable,
    IReadOnlyDictionary<string, EntitlementValueDto> Entitlements);

/// <summary>What is for sale, and whether anything is.</summary>
public sealed record CatalogueDto(
    bool Enabled,
    string Currency,
    IReadOnlyList<CataloguePlanDto> Plans,
    IReadOnlyDictionary<string, IReadOnlyList<EntitlementRungDto>> Ladders);

/// <summary>One recurring relationship, as its customer reads it.</summary>
public sealed record SubscriptionDto(
    string Id,
    [property: JsonConverter(typeof(LowercaseSubjectKindConverter))] SubjectKind SubjectKind,
    string SubjectId,
    string PlanName,
    string PlanDisplayName,
    int VersionNumber,
    string Status,
    DateTimeOffset? CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    DateTimeOffset? GracePeriodEndsAt,
    long? PriceMinorUnits,
    string? Currency,
    string? Interval,
    bool IsPayer)
{
    /// <summary>Mirrors <c>StripeCatalogueSync.MonthlyInterval</c>, spelled out here for the same
    /// reason <c>CheckoutCatalogueService</c> spells out its own copy: the DTO layer does not have to
    /// know about the Stripe seam to say "month". What keeps the three honest is a test that reads the
    /// interval off a subscription and off the catalogue entry for the same plan version and demands
    /// they match, rather than three comments promising they do.</summary>
    private const string StripeCatalogueSyncInterval = "month";

    public static SubscriptionDto From(
        Subscription subscription, Plan plan, PlanVersion? version, bool isPayer)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(plan);

        return new SubscriptionDto(
            subscription.StripeSubscriptionId,
            subscription.SubjectKind,
            subscription.SubjectId,
            plan.Name,
            plan.DisplayName ?? plan.Name,
            subscription.VersionNumber,
            SubscriptionStatusWire.Of(subscription.Status),
            subscription.CurrentPeriodEnd,
            subscription.CancelAtPeriodEnd,
            subscription.GracePeriodEndsAt,
            version?.PriceMinorUnits,
            version?.Currency,
            version is null ? null : StripeCatalogueSyncInterval,
            isPayer);
    }
}

/// <summary>The answer to starting a subscription.</summary>
public sealed record CreateSubscriptionResponse(SubscriptionDto Subscription, string? ClientSecret);

public sealed record ProrationLineDto(string Description, long AmountMinorUnits);

/// <summary>What a plan change costs, before it is committed.</summary>
public sealed record ProrationPreviewDto(
    long ImmediateChargeMinorUnits,
    string Currency,
    long NextInvoiceTotalMinorUnits,
    DateTimeOffset? NextInvoiceAt,
    IReadOnlyList<ProrationLineDto> Lines);

/// <summary>A card.</summary>
public sealed record PaymentMethodDto(
    string Id,
    string? Brand,
    string? Last4,
    long? ExpMonth,
    long? ExpYear,
    bool IsDefault);

/// <summary>The secret the Payment Element mounts against when a card is added outside a
/// purchase.</summary>
public sealed record SetupIntentDto(string? ClientSecret);

/// <summary>One invoice. The two URLs are opened externally; we do not render invoices.</summary>
public sealed record InvoiceDto(
    string Id,
    string? Number,
    string? Status,
    long AmountDueMinorUnits,
    string Currency,
    DateTimeOffset CreatedAt,
    string? HostedInvoiceUrl,
    string? InvoicePdfUrl);
