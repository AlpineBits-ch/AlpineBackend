using System.Security.Cryptography;
using System.Text;
using Echo.Entitlements.Model;

namespace Echo.Entitlements.Resolution;

/// <summary>Where the plan catalogue comes from.</summary>
public interface IPlanCatalogueSource
{
    /// <summary>
    /// A short, cheap, allocation-free name for "the catalogue this source last answered with".
    /// </summary>
    string Revision { get; }

    ValueTask<PlanCatalogue> CurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>Drops whatever is memoised, so the next read goes back to the source.</summary>
    void Invalidate();
}

/// <summary>The catalogue an instance was configured with, and nothing else.</summary>
public sealed class FixedPlanCatalogueSource : IPlanCatalogueSource
{
    private readonly PlanCatalogue _catalogue;

    public FixedPlanCatalogueSource(PlanCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        _catalogue = catalogue;
        Revision = PlanCatalogueRevision.Of(catalogue);
    }

    public string Revision { get; }

    public ValueTask<PlanCatalogue> CurrentAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_catalogue);

    /// <summary>Nothing to drop: this catalogue changes when the process is redeployed with a
    /// different configuration file, and not otherwise.</summary>
    public void Invalidate()
    {
    }
}

/// <summary>A stable short name for the contents of a catalogue.</summary>
public static class PlanCatalogueRevision
{
    /// <summary>What a catalogue that has never been read from anywhere hashes to.</summary>
    public const string Unknown = "00000000";

    public static string Of(PlanCatalogue? catalogue)
    {
        if (catalogue is null) return Unknown;

        var text = new StringBuilder();

        foreach (var plan in catalogue.Plans.OrderBy(plan => plan.Name, StringComparer.Ordinal))
        {
            text.Append(plan.Name).Append('~').Append(plan.DisplayName).Append(':');

            foreach (var (key, value) in plan.Values.OrderBy(entry => entry.Key.Name, StringComparer.Ordinal))
            {
                text.Append(key.Name).Append('=').Append(key.Format(value)).Append(',');
            }

            text.Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }
}
