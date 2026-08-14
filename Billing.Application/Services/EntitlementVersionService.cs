using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Model;
using Echo.Entitlements.Wire;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Services;

/// <summary>The only thing that moves a subject's entitlement version.</summary>
public sealed class EntitlementVersionService(MicroserviceContext db) : IEntitlementVersionProvider
{
    /// <summary>
    /// The table and column names are literals because raw SQL cannot ask the model for them, and
    /// they come from <c>UseSnakeCaseNamingConvention</c> rather than from anything declared by
    /// hand.
    /// </summary>
    private const string AdvanceSql =
        """
        INSERT INTO entitlement_versions (id, subject_kind, subject_id, version, created_at, updated_at)
        VALUES ({0}, {1}, {2}, 1, {3}, {3})
        ON CONFLICT (subject_kind, subject_id)
        DO UPDATE SET version = entitlement_versions.version + 1, updated_at = {3}
        RETURNING version AS "Value"
        """;

    /// <summary>The version this subject is at now.</summary>
    public async ValueTask<long> VersionAsync(
        EntitlementSubject subject, CancellationToken cancellationToken = default)
    {
        var kind = subject.Kind;
        var id = subject.Id;

        var row = await db.EntitlementVersions
            .AsNoTracking()
            .Where(v => v.SubjectKind == kind && v.SubjectId == id)
            .Select(v => (long?)v.Version)
            .FirstOrDefaultAsync(cancellationToken);

        return row ?? 0;
    }

    /// <summary>Moves the subject to its next version and returns it.</summary>
    public async Task<long> AdvanceAsync(EntitlementSubject subject, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var versions = await db.Database
            .SqlQueryRaw<long>(
                AdvanceSql,
                Domain.Aggregates.EntitlementVersion.GenerateId(),
                subject.Kind.ToString(),
                subject.Id,
                now)
            .ToListAsync(cancellationToken);

        return versions[0];
    }
}
