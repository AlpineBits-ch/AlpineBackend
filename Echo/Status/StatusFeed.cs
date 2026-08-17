using System.Globalization;
using System.Text;
using System.Xml;
using Echo.Domain.Entities.Status;

namespace Echo.Status;

/// <summary>The Atom rendering of incident history.</summary>
public static class StatusFeed
{
    /// <summary>Renders incidents, newest first, as an Atom 1.0 document.</summary>
    /// <param name="incidents">The incidents to publish, already ordered and filtered.</param>
    /// <param name="baseUrl">Absolute base URL of the status site, with no trailing slash.</param>
    /// <param name="now">Fallback feed timestamp when there is nothing to publish.</param>
    /// <returns>The feed as XML.</returns>
    public static string Render(
        IReadOnlyList<StatusIncident> incidents, string baseUrl, DateTimeOffset now)
    {
        var builder = new StringBuilder();
        var updated = incidents.Count > 0 ? incidents.Max(i => i.UpdatedAt) : now;

        // Synchronous throughout: an XmlWriter left on the default Async = false throws from
        // DisposeAsync, so this must never become an `await using`.
        using (var writer = new Utf8StringWriter(builder))
        using (var xml = XmlWriter.Create(writer, new XmlWriterSettings { Indent = true }))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("feed", "http://www.w3.org/2005/Atom");

            xml.WriteElementString("id", baseUrl + "/");
            xml.WriteElementString("title", "venta platform status");
            xml.WriteElementString("updated", Timestamp(updated));

            xml.WriteStartElement("link");
            xml.WriteAttributeString("rel", "alternate");
            xml.WriteAttributeString("href", baseUrl + "/");
            xml.WriteEndElement();

            foreach (var incident in incidents)
            {
                var url = StatusSnapshotBuilder.IncidentUrl(incident.Reference);
                var latest = incident.Updates.MaxBy(u => u.PostedAt);

                xml.WriteStartElement("entry");
                xml.WriteElementString("id", url);
                xml.WriteElementString("title", incident.Title);
                xml.WriteElementString("updated", Timestamp(incident.UpdatedAt));
                xml.WriteElementString("published", Timestamp(incident.StartedAt));

                xml.WriteStartElement("link");
                xml.WriteAttributeString("rel", "alternate");
                xml.WriteAttributeString("href", url);
                xml.WriteEndElement();

                // Plain text, not HTML.
                xml.WriteStartElement("content");
                xml.WriteAttributeString("type", "text");
                xml.WriteString(latest?.Body ?? string.Empty);
                xml.WriteEndElement();

                xml.WriteEndElement();
            }

            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return builder.ToString();
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>
    /// A StringWriter reports UTF-16, which XmlWriter then puts in the prolog while the response is
    /// served as UTF-8.
    /// </summary>
    private sealed class Utf8StringWriter(StringBuilder builder)
        : StringWriter(builder, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
