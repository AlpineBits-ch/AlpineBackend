using Billing.Application.Dtos;

namespace Billing.Application.Promotions;

/// <summary>What the client posts to start a trial.</summary>
public sealed record StartTrialRequest(
    string CampaignCode,
    string? GuildId = null,
    string? PaymentMethodId = null);

/// <summary>Where to move a trial.</summary>
public sealed record MoveTrialRequest(string GuildId);

/// <summary>A started trial.</summary>
public sealed record StartTrialResponse(
    string CampaignCode,
    string Plan,
    DateTimeOffset? TrialEndsAt,
    SubscriptionDto Subscription,
    string? ClientSecret);

/// <summary>A moved trial.</summary>
public sealed record MoveTrialResponse(
    string CampaignCode,
    string GuildId,
    DateTimeOffset? TrialEndsAt);

/// <summary>The preflight answer.</summary>
public sealed record TrialEligibilityDto(string CampaignCode, bool Eligible);
