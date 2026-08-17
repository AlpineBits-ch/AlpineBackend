using System.Xml.Linq;
using Echo.Domain.Entities.Status;
using Echo.Domain.Enums;
using Echo.Status;

namespace Echo.Tests.Status;

/// <summary>The Atom feed, which no other status endpoint exercises.</summary>
[TestFixture]
[Category("Unit")]
public class StatusFeedTests
{
    private const string BaseUrl = "https://status.venta.gg";

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private static StatusIncident Incident(string title, string body, DateTimeOffset startedAt)
    {
        var incident = StatusIncident.Create(
            new CreateIncidentParams { Title = title, Body = body }, startedAt);

        return incident;
    }

    // ── The shape of the document ─────────────────────────────────────────────

    [Test]
    public void An_incident_becomes_an_entry()
    {
        var feed = StatusFeed.Render([Incident("Voice is down", "We are investigating.", Now)], BaseUrl, Now);

        var entries = XDocument.Parse(feed).Root!.Elements(Atom + "entry").ToList();

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Element(Atom + "title")!.Value, Is.EqualTo("Voice is down"));
            Assert.That(entries[0].Element(Atom + "content")!.Value, Is.EqualTo("We are investigating."));
            Assert.That(entries[0].Element(Atom + "content")!.Attribute("type")!.Value, Is.EqualTo("text"));
        });
    }

    /// <summary>The prolog has to agree with the charset the response is served under.</summary>
    [Test]
    public void The_declaration_says_utf_8()
    {
        var feed = StatusFeed.Render([Incident("Voice is down", "Investigating.", Now)], BaseUrl, Now);

        Assert.That(XDocument.Parse(feed).Declaration!.Encoding, Is.EqualTo("utf-8").IgnoreCase);
    }

    [Test]
    public void The_latest_update_is_the_entry_content()
    {
        var incident = Incident("Voice is down", "We are investigating.", Now);
        incident.PostUpdate(IncidentStatus.Resolved, "Voice is back.", null, "usr_1", Now.AddHours(1));

        var feed = StatusFeed.Render([incident], BaseUrl, Now);

        var entry = XDocument.Parse(feed).Root!.Element(Atom + "entry")!;

        Assert.That(entry.Element(Atom + "content")!.Value, Is.EqualTo("Voice is back."));
    }

    // ── Edges ─────────────────────────────────────────────────────────────────

    /// <summary>A fresh instance has no history, and the feed still has to parse.</summary>
    [Test]
    public void An_empty_feed_is_well_formed_and_stamped_with_now()
    {
        var feed = StatusFeed.Render([], BaseUrl, Now);

        var root = XDocument.Parse(feed).Root!;

        Assert.Multiple(() =>
        {
            Assert.That(root.Elements(Atom + "entry"), Is.Empty);
            Assert.That(root.Element(Atom + "updated")!.Value, Is.EqualTo("2026-08-17T12:00:00Z"));
            Assert.That(root.Element(Atom + "id")!.Value, Is.EqualTo(BaseUrl + "/"));
        });
    }

    /// <summary>The feed timestamp is the newest incident's, not the caller's clock.</summary>
    [Test]
    public void The_feed_is_stamped_with_the_newest_incident()
    {
        var older = Incident("Older", "Body.", Now.AddDays(-2));
        var newer = Incident("Newer", "Body.", Now.AddHours(-1));

        var feed = StatusFeed.Render([newer, older], BaseUrl, Now.AddDays(10));

        var root = XDocument.Parse(feed).Root!;

        Assert.That(root.Element(Atom + "updated")!.Value, Is.EqualTo("2026-08-17T11:00:00Z"));
    }

    /// <summary>Markup in a staff-written title must not break the document.</summary>
    [Test]
    public void A_title_with_markup_is_escaped()
    {
        var incident = Incident("Search & <filters> are down", "Body.", Now);

        var feed = StatusFeed.Render([incident], BaseUrl, Now);

        var entry = XDocument.Parse(feed).Root!.Element(Atom + "entry")!;

        Assert.That(entry.Element(Atom + "title")!.Value, Is.EqualTo("Search & <filters> are down"));
    }

    /// <summary>An incident whose timeline was not loaded still renders.</summary>
    [Test]
    public void An_incident_with_no_updates_gets_empty_content()
    {
        var incident = Incident("Voice is down", "Investigating.", Now);
        incident.Updates.Clear();

        var feed = StatusFeed.Render([incident], BaseUrl, Now);

        var entry = XDocument.Parse(feed).Root!.Element(Atom + "entry")!;

        Assert.That(entry.Element(Atom + "content")!.Value, Is.Empty);
    }
}
