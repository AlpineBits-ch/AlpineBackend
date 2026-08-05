using System.Reflection;
using Echo.Domain.Entities.Status;
using Echo.Domain.Enums;
using Echo.Proxy;
using Echo.Status;

namespace Echo.Tests.Status;

/// <summary>
/// What leaves the gateway on the public status endpoints, and the catalog that decides what is
/// watched at all.
/// </summary>
[TestFixture]
[Category("Unit")]
public class StatusPayloadTests
{
    // ── Nothing staff-only escapes ────────────────────────────────────────────

    /// <summary>Names that must never appear on a public payload.</summary>
    private static readonly string[] StaffOnlyNames =
    [
        "DetectionDetail", "Confirmed", "IsRetracted", "Origin", "Clusters",
        "DegradedRate", "OutageRate", "MinimumVolume", "CreatedByUserId", "AuthorUserId",
        "Id", "ComponentId", "IncidentId", "Position", "IsVisible", "ImpactHint",
    ];

    [Test]
    public void No_public_status_payload_carries_a_staff_only_field()
    {
        var types = new[]
        {
            typeof(StatusSummaryDto), typeof(StatusBannerDto), typeof(StatusComponentDto),
            typeof(StatusIncidentDto), typeof(StatusUpdateDto),
            typeof(StatusUptimeDto), typeof(StatusUptimeComponentDto), typeof(StatusUptimeDayDto),
        };

        var offenders = types
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => StaffOnlyNames.Contains(property.Name))
                .Select(property => $"{type.Name}.{property.Name}"))
            .ToList();

        Assert.That(offenders, Is.Empty,
            "these are staff-only and would be published to anonymous callers: " + string.Join(", ", offenders));
    }

    /// <summary>The entity does carry them - which is exactly why the DTOs are hand-written rather
    /// than the entity being serialised straight out.</summary>
    [Test]
    public void The_entity_does_carry_the_staff_only_fields_the_payload_omits()
    {
        var properties = typeof(StatusIncident).GetProperties().Select(p => p.Name).ToList();

        Assert.That(properties, Does.Contain(nameof(StatusIncident.DetectionDetail)));
        Assert.That(properties, Does.Contain(nameof(StatusIncident.Confirmed)));
    }

    // ── Wire vocabulary ───────────────────────────────────────────────────────

    [TestCase(ComponentStatus.Operational, "operational")]
    [TestCase(ComponentStatus.DegradedPerformance, "degraded_performance")]
    [TestCase(ComponentStatus.PartialOutage, "partial_outage")]
    [TestCase(ComponentStatus.MajorOutage, "major_outage")]
    [TestCase(ComponentStatus.UnderMaintenance, "under_maintenance")]
    public void Component_statuses_go_over_the_wire_as_snake_case(ComponentStatus status, string expected)
    {
        Assert.That(StatusText.Slug(status), Is.EqualTo(expected));
    }

    [TestCase(IncidentStatus.Investigating, "investigating")]
    [TestCase(IncidentStatus.InProgress, "in_progress")]
    [TestCase(IncidentStatus.Resolved, "resolved")]
    public void Incident_statuses_go_over_the_wire_as_snake_case(IncidentStatus status, string expected)
    {
        Assert.That(StatusText.Slug(status), Is.EqualTo(expected));
    }

    // ── The overall indicator ─────────────────────────────────────────────────

    [Test]
    public void An_all_clear_estate_is_operational()
    {
        var indicator = StatusText.Indicator([ComponentStatus.Operational, ComponentStatus.Operational]);

        Assert.That(indicator, Is.EqualTo(StatusIndicator.Operational));
    }

    [Test]
    public void The_worst_component_decides_the_indicator()
    {
        var indicator = StatusText.Indicator(
        [
            ComponentStatus.Operational,
            ComponentStatus.DegradedPerformance,
            ComponentStatus.MajorOutage,
        ]);

        Assert.That(indicator, Is.EqualTo(StatusIndicator.MajorOutage));
    }

    [Test]
    public void Maintenance_alone_reads_as_maintenance()
    {
        var indicator = StatusText.Indicator([ComponentStatus.Operational, ComponentStatus.UnderMaintenance]);

        Assert.That(indicator, Is.EqualTo(StatusIndicator.Maintenance));
    }

    /// <summary>Nobody wants "under maintenance" on the banner while sign-in is down.</summary>
    [Test]
    public void Maintenance_never_wins_over_something_actually_broken()
    {
        var indicator = StatusText.Indicator([ComponentStatus.UnderMaintenance, ComponentStatus.MajorOutage]);

        Assert.That(indicator, Is.EqualTo(StatusIndicator.MajorOutage));
    }

    [Test]
    public void An_empty_estate_is_operational_rather_than_unknown()
    {
        Assert.That(StatusText.Indicator([]), Is.EqualTo(StatusIndicator.Operational));
    }

    // ── Impact drives what an incident claims ─────────────────────────────────

    [TestCase(IncidentImpact.Minor, ComponentStatus.DegradedPerformance)]
    [TestCase(IncidentImpact.Major, ComponentStatus.PartialOutage)]
    [TestCase(IncidentImpact.Critical, ComponentStatus.MajorOutage)]
    [TestCase(IncidentImpact.None, ComponentStatus.Operational)]
    public void Impact_decides_what_an_incident_asserts_about_its_components(
        IncidentImpact impact, ComponentStatus expected)
    {
        Assert.That(StatusText.AssertedStatus(impact, IncidentKind.Incident), Is.EqualTo(expected));
    }

    [Test]
    public void Maintenance_asserts_maintenance_whatever_its_impact()
    {
        Assert.That(StatusText.AssertedStatus(IncidentImpact.Critical, IncidentKind.Maintenance),
            Is.EqualTo(ComponentStatus.UnderMaintenance));
    }

    [Test]
    public void Maintenance_is_always_an_informational_banner()
    {
        Assert.That(StatusText.Severity(IncidentImpact.Critical, IncidentKind.Maintenance), Is.EqualTo("info"));
    }

    // ── Catalog coverage ──────────────────────────────────────────────────────

    /// <summary>Every proxied backend is watched by something.</summary>
    [Test]
    public void Every_proxy_cluster_is_watched_by_a_status_component()
    {
        var clusters = ProxyConfig.GetClusters().Select(c => c.ClusterId).Distinct().ToList();
        var watched = StatusComponentCatalog.AllClusters;

        var unwatched = clusters.Except(watched).ToList();

        Assert.That(unwatched, Is.Empty,
            "an outage of these would never appear on the status page: " + string.Join(", ", unwatched));
    }

    [Test]
    public void Every_watched_cluster_is_one_the_proxy_actually_defines()
    {
        var clusters = ProxyConfig.GetClusters().Select(c => c.ClusterId).Distinct().ToList();

        var unknown = StatusComponentCatalog.AllClusters.Except(clusters).ToList();

        Assert.That(unknown, Is.Empty,
            "these components watch clusters that do not exist, so they can never report: "
            + string.Join(", ", unknown));
    }

    [Test]
    public void Component_keys_are_unique()
    {
        var keys = StatusComponentCatalog.Seeds.Select(s => s.Key).ToList();

        Assert.That(keys, Is.Unique, "the seed is matched on key, so a duplicate would insert forever");
    }

    /// <summary>
    /// The impact sentence is what every automatically generated incident is built from.
    /// </summary>
    [Test]
    public void Every_seeded_component_has_an_impact_sentence()
    {
        Assert.Multiple(() =>
        {
            foreach (var seed in StatusComponentCatalog.Seeds)
            {
                Assert.That(seed.ImpactHint, Is.Not.Empty, $"{seed.Key} has no impact sentence");
                Assert.That(seed.Name, Is.Not.Empty);
            }
        });
    }

    /// <summary>The two gateway-local components are the only ones allowed to watch nothing, because
    /// their signal is the gateway's own.</summary>
    [Test]
    public void Only_the_gateway_local_components_watch_no_cluster()
    {
        var unmonitored = StatusComponentCatalog.Seeds
            .Where(s => s.Clusters.Count == 0)
            .Select(s => s.Key)
            .ToList();

        Assert.That(unmonitored, Is.EquivalentTo(new[]
        {
            StatusComponentCatalog.GatewayKey,
            StatusComponentCatalog.RealtimeKey,
        }));
    }

    /// <summary>The public name is what a user reads.</summary>
    [Test]
    public void No_component_is_named_after_a_service()
    {
        string[] internalNames = ["identity", "guild", "messaging", "social", "unfurl", "echo", "cluster"];

        Assert.Multiple(() =>
        {
            foreach (var seed in StatusComponentCatalog.Seeds)
            {
                foreach (var name in internalNames)
                {
                    Assert.That(seed.Name.ToLowerInvariant(), Does.Not.Contain(name),
                        $"{seed.Key} is named after an internal service");
                }
            }
        });
    }
}
