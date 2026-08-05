using Echo.Domain.Entities.Status;
using Echo.Domain.Enums;

namespace Echo.Tests.Status;

/// <summary>
/// The incident record's own rules: who owns it, what the detector may do to it, and how technical
/// the generated copy is allowed to be.
/// </summary>
[TestFixture]
[Category("Unit")]
public class IncidentLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static StatusComponent Component() => StatusComponent.Create(
        new StatusComponentSeed("accounts", "Sign-in and accounts", "Signing in",
            "Some people may not be able to sign in or create an account.", ["identity-cluster"], 10),
        Now);

    private static StatusIncident Automatic(string template = IncidentTemplates.ElevatedErrors) =>
        StatusIncident.CreateAutomatic(Component(), template, IncidentImpact.Minor, "detail", Now);

    // ── Generated copy ────────────────────────────────────────────────────────

    /// <summary>The rule the whole feature turns on, asserted rather than trusted.</summary>
    [TestCase(IncidentTemplates.ElevatedErrors)]
    [TestCase(IncidentTemplates.Unavailable)]
    [TestCase(IncidentTemplates.Recovered)]
    public void Generated_copy_carries_no_numbers(string template)
    {
        var component = Component();

        Assert.Multiple(() =>
        {
            Assert.That(IncidentTemplates.Title(template, component), Does.Not.Match(@"\d"));
            Assert.That(IncidentTemplates.Body(template, component), Does.Not.Match(@"\d"));
        });
    }

    /// <summary>The public copy must never name a cluster, a service or the gateway project. A user
    /// reading "identity-cluster" learns nothing and an outsider learns our topology.</summary>
    [TestCase(IncidentTemplates.ElevatedErrors)]
    [TestCase(IncidentTemplates.Unavailable)]
    [TestCase(IncidentTemplates.Recovered)]
    public void Generated_copy_names_no_internal_component(string template)
    {
        var component = Component();
        var text = IncidentTemplates.Title(template, component) + " " + IncidentTemplates.Body(template, component);

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Not.Contain("cluster"));
            Assert.That(text, Does.Not.Contain("identity"));
            Assert.That(text.ToLowerInvariant(), Does.Not.Contain("echo"));
        });
    }

    [Test]
    public void A_generated_incident_opens_as_investigating_with_its_first_update_already_posted()
    {
        var incident = Automatic();

        Assert.That(incident.Status, Is.EqualTo(IncidentStatus.Investigating),
            "a monitor knows something is failing and cannot know why");
        Assert.That(incident.Updates, Has.Count.EqualTo(1),
            "an incident with no first update is a red banner with nothing under it");
        Assert.That(incident.Origin, Is.EqualTo(IncidentOrigin.Automatic));
        Assert.That(incident.Template, Is.EqualTo(IncidentTemplates.ElevatedErrors));
    }

    /// <summary>The detection detail is what the console shows and the public payload never does.</summary>
    [Test]
    public void A_generated_incident_keeps_the_measurements_off_the_public_copy()
    {
        var incident = StatusIncident.CreateAutomatic(
            Component(), IncidentTemplates.ElevatedErrors, IncidentImpact.Minor,
            "31/100 responses failed (31.0%)", Now);

        Assert.That(incident.DetectionDetail, Does.Contain("31.0%"));
        Assert.That(incident.Title, Does.Not.Contain("31"));
        Assert.That(incident.Updates.Single().Body, Does.Not.Contain("31"));
    }

    // ── Ownership ─────────────────────────────────────────────────────────────

    /// <summary>A staff-written incident belongs to a person from the first keystroke, so nothing
    /// automatic may ever touch it.</summary>
    [Test]
    public void A_staff_incident_is_confirmed_from_birth()
    {
        var incident = StatusIncident.Create(new CreateIncidentParams
        {
            Title = "Messages are slow",
            Body = "We are looking into it.",
            CreatedByUserId = "user_1",
        }, Now);

        Assert.That(incident.Confirmed, Is.True);
        Assert.That(incident.CanBeAutomaticallyClosed, Is.False);
    }

    [Test]
    public void A_generated_incident_starts_unconfirmed_and_is_the_detectors_to_close()
    {
        var incident = Automatic();

        Assert.That(incident.Confirmed, Is.False);
        Assert.That(incident.CanBeAutomaticallyClosed, Is.True);
    }

    /// <summary>The handover.</summary>
    [Test]
    public void A_staff_update_takes_ownership_of_a_generated_incident()
    {
        var incident = Automatic();

        incident.PostUpdate(IncidentStatus.Identified, "It is the database.", null, "user_1", Now.AddMinutes(5));

        Assert.That(incident.Confirmed, Is.True);
        Assert.That(incident.CanBeAutomaticallyClosed, Is.False);
    }

    [Test]
    public void An_automatic_update_does_not_take_ownership()
    {
        var incident = Automatic();

        incident.PostUpdate(IncidentStatus.Investigating, "Still failing.",
            IncidentTemplates.ElevatedErrors, authorUserId: null, Now.AddMinutes(1));

        Assert.That(incident.Confirmed, Is.False);
        Assert.That(incident.CanBeAutomaticallyClosed, Is.True);
    }

    [Test]
    public void Confirming_is_one_way()
    {
        var incident = Automatic();

        incident.Confirm(Now.AddMinutes(1));
        incident.Confirm(Now.AddMinutes(2));

        Assert.That(incident.Confirmed, Is.True);
    }

    // ── Timeline ──────────────────────────────────────────────────────────────

    [Test]
    public void Updates_accumulate_rather_than_replace()
    {
        var incident = Automatic();

        incident.PostUpdate(IncidentStatus.Identified, "Cause found.", null, "user_1", Now.AddMinutes(5));
        incident.PostUpdate(IncidentStatus.Monitoring, "Fix is out.", null, "user_1", Now.AddMinutes(9));

        Assert.That(incident.Updates, Has.Count.EqualTo(3), "a correction is another entry, never an edit");
        Assert.That(incident.Status, Is.EqualTo(IncidentStatus.Monitoring));
    }

    [Test]
    public void Resolving_stamps_the_resolution_time_and_closes_the_incident()
    {
        var incident = Automatic();
        var resolved = Now.AddMinutes(20);

        incident.PostUpdate(IncidentStatus.Resolved, "Over.", null, "user_1", resolved);

        Assert.That(incident.ResolvedAt, Is.EqualTo(resolved));
        Assert.That(incident.IsOpen, Is.False);
    }

    /// <summary>A reopen has to clear the resolution stamp, or the incident stays filtered out of
    /// every "currently active" query while showing an in-flight state.</summary>
    [Test]
    public void Reopening_clears_the_resolution_time()
    {
        var incident = Automatic();

        incident.PostUpdate(IncidentStatus.Resolved, "Over.", null, "user_1", Now.AddMinutes(20));
        incident.PostUpdate(IncidentStatus.Investigating, "It is back.", null, "user_1", Now.AddMinutes(25));

        Assert.That(incident.ResolvedAt, Is.Null);
        Assert.That(incident.IsOpen, Is.True);
    }

    /// <summary>A resolution time is set once.</summary>
    [Test]
    public void A_second_resolution_does_not_move_the_first_one()
    {
        var incident = Automatic();
        var first = Now.AddMinutes(20);

        incident.PostUpdate(IncidentStatus.Resolved, "Over.", null, "user_1", first);
        incident.PostUpdate(IncidentStatus.Resolved, "Still over.", null, "user_1", Now.AddMinutes(40));

        Assert.That(incident.ResolvedAt, Is.EqualTo(first));
    }

    // ── Retraction ────────────────────────────────────────────────────────────

    /// <summary>Retracted is hidden, not deleted.</summary>
    [Test]
    public void Retracting_hides_the_incident_without_destroying_it()
    {
        var incident = Automatic();

        incident.PostUpdate(IncidentStatus.Resolved, "Recovered.", IncidentTemplates.Recovered, null, Now.AddMinutes(1));
        incident.Retract(Now.AddMinutes(1));

        Assert.That(incident.IsRetracted, Is.True);
        Assert.That(incident.Updates, Is.Not.Empty);
        Assert.That(incident.Reference, Is.Not.Null);
    }

    // ── Components ────────────────────────────────────────────────────────────

    [Test]
    public void Setting_components_replaces_the_previous_set_rather_than_adding_to_it()
    {
        var incident = Automatic();

        incident.SetComponents([("stcp_a", ComponentStatus.DegradedPerformance)], Now);
        incident.SetComponents([("stcp_b", ComponentStatus.MajorOutage)], Now);

        Assert.That(incident.Components, Has.Count.EqualTo(1));
        Assert.That(incident.Components.Single().ComponentId, Is.EqualTo("stcp_b"));
    }

    [Test]
    public void A_closed_incident_can_assert_nothing()
    {
        var incident = Automatic();

        incident.SetComponents([("stcp_a", ComponentStatus.MajorOutage)], Now);
        incident.SetComponents([], Now.AddMinutes(30));

        Assert.That(incident.Components, Is.Empty);
    }

    // ── Rollups ───────────────────────────────────────────────────────────────

    [Test]
    public void A_day_with_nothing_recorded_has_no_uptime_rather_than_a_hundred_percent()
    {
        var rollup = StatusDayRollup.Create("stcp_a", new DateOnly(2026, 8, 5), Now);

        Assert.That(rollup.Uptime, Is.Null, "\"we have no idea\" and \"it was fine\" are different answers");
    }

    /// <summary>An announced window is not an outage.</summary>
    [Test]
    public void Maintenance_counts_as_up()
    {
        var rollup = StatusDayRollup.Create("stcp_a", new DateOnly(2026, 8, 5), Now);
        rollup.Add(ComponentStatus.UnderMaintenance, 3600, Now);

        Assert.That(rollup.Uptime, Is.EqualTo(1).Within(0.0001));
    }

    [Test]
    public void An_outage_pulls_the_day_down_in_proportion_to_its_length()
    {
        var rollup = StatusDayRollup.Create("stcp_a", new DateOnly(2026, 8, 5), Now);
        rollup.Add(ComponentStatus.Operational, 900, Now);
        rollup.Add(ComponentStatus.MajorOutage, 100, Now);

        Assert.That(rollup.Uptime, Is.EqualTo(0.9).Within(0.0001));
    }

    [Test]
    public void Both_outage_levels_land_in_the_same_bucket()
    {
        var rollup = StatusDayRollup.Create("stcp_a", new DateOnly(2026, 8, 5), Now);
        rollup.Add(ComponentStatus.PartialOutage, 60, Now);
        rollup.Add(ComponentStatus.MajorOutage, 60, Now);

        Assert.That(rollup.OutageSeconds, Is.EqualTo(120));
    }
}
