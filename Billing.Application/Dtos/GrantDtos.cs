using Billing.Domain.Aggregates;
using Echo.Entitlements.Model;

namespace Billing.Application.Dtos;

/// <summary>What the console posts to issue a grant.</summary>
public sealed record IssueGrantRequest(
    SubjectKind SubjectKind,
    string SubjectId,
    GrantKind GrantKind,
    string? Plan,
    Dictionary<string, string>? Entitlements,
    DateTimeOffset? ExpiresAt,
    string Reason,
    GrantSource Source);

public sealed record RevokeGrantRequest(string Reason);

/// <summary>Null <paramref name="ExpiresAt"/> converts the grant to a permanent one, which section 6
/// lists as an operation the console needs.</summary>
public sealed record AmendGrantExpiryRequest(DateTimeOffset? ExpiresAt);

/// <summary>A grant as the console reads it.</summary>
public sealed record GrantDto(
    string Id,
    SubjectKind SubjectKind,
    string SubjectId,
    GrantKind GrantKind,
    string? Plan,
    IReadOnlyDictionary<string, string>? Entitlements,
    DateTimeOffset? ExpiresAt,
    string Reason,
    GrantSource Source,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt,
    string? RevokedBy,
    string? RevokeReason,
    bool IsActive)
{
    public static GrantDto From(Grant grant, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(grant);

        var wire = Services.GrantService.ToEntitlementGrant(grant);

        return new GrantDto(
            grant.Id,
            grant.SubjectKind,
            grant.SubjectId,
            grant.GrantKind,
            grant.Plan,
            wire.Entitlements,
            grant.ExpiresAt,
            grant.Reason,
            grant.Source,
            grant.CreatedBy,
            grant.CreatedAt,
            grant.RevokedAt,
            grant.RevokedBy,
            grant.RevokeReason,
            grant.IsActiveAt(now));
    }
}

/// <summary>The subject's grants plus the version the entitlement snapshot will report, so the
/// console shows one consistent state rather than two reads that can disagree.</summary>
public sealed record GrantListDto(
    SubjectKind SubjectKind,
    string SubjectId,
    long Version,
    IReadOnlyList<GrantDto> Grants);

/// <summary>A refusal the console can switch on.</summary>
public sealed record GrantErrorDto(string Code, string Message);
