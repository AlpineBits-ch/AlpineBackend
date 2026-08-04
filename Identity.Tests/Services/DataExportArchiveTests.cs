using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Identity.Application.Services.DataExport;
using Identity.Contracts.Bus.Commands;

namespace Identity.Tests.Services;

/// <summary>
/// The zip and its <c>manifest.json</c> (T1-7).
///
/// <para>Assembly is a static method over a list precisely so these are assertions rather than
/// things inferred from a bucket - the same split <c>RetentionSweep</c> has from its hosted
/// service.</para>
/// </summary>
[TestFixture]
public class DataExportArchiveTests
{
    private static UserDataExportFragment Fragment(string service, string json, params (string, int)[] counts) =>
        new()
        {
            Service = service,
            FragmentJson = json,
            RowCounts = counts.ToDictionary(c => c.Item1, c => c.Item2),
        };

    private static Dictionary<string, string> Unzip(byte[] archive)
    {
        using var stream = new MemoryStream(archive);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        return zip.Entries.ToDictionary(
            e => e.FullName,
            e =>
            {
                using var entryStream = e.Open();
                using var reader = new StreamReader(entryStream, Encoding.UTF8);
                return reader.ReadToEnd();
            });
    }

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public void Build_WritesOneFilePerServicePlusAManifestNamingEachProducer()
    {
        var result = DataExportArchive.Build(
            "dxrq_1", "user_1",
            [
                Fragment("identity", """{"account":{"id":"user_1"}}""", ("account", 1)),
                Fragment("social", """{"profile":{}}""", ("profile", 1), ("relationships", 3)),
            ],
            DateTimeOffset.UnixEpoch);

        var files = Unzip(result.Content);

        Assert.Multiple(() =>
        {
            Assert.That(files.Keys, Does.Contain("manifest.json"));
            Assert.That(files.Keys, Does.Contain("identity.json"));
            Assert.That(files.Keys, Does.Contain("social.json"));
            Assert.That(result.TotalRows, Is.EqualTo(5));
        });

        using var manifest = JsonDocument.Parse(files["manifest.json"]);
        var services = manifest.RootElement.GetProperty("services").EnumerateArray().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(manifest.RootElement.GetProperty("exportId").GetString(), Is.EqualTo("dxrq_1"));
            Assert.That(manifest.RootElement.GetProperty("userId").GetString(), Is.EqualTo("user_1"));
            Assert.That(manifest.RootElement.GetProperty("formatVersion").GetInt32(),
                Is.EqualTo(DataExportArchive.FormatVersion));
            Assert.That(services, Has.Count.EqualTo(2));

            // Named producer and named file, per fragment - that is what makes the archive
            // self-describing rather than a bag of json.
            Assert.That(services[0].GetProperty("service").GetString(), Is.EqualTo("identity"));
            Assert.That(services[0].GetProperty("file").GetString(), Is.EqualTo("identity.json"));
            Assert.That(services[1].GetProperty("rowCounts").GetProperty("relationships").GetInt32(),
                Is.EqualTo(3));
        });
    }

    [Test]
    public void Build_OrdersServicesDeterministically()
    {
        var first = DataExportArchive.Build("dxrq_2", "user_1",
            [Fragment("social", "{}"), Fragment("bots", "{}"), Fragment("identity", "{}")],
            DateTimeOffset.UnixEpoch);

        var second = DataExportArchive.Build("dxrq_2", "user_1",
            [Fragment("identity", "{}"), Fragment("social", "{}"), Fragment("bots", "{}")],
            DateTimeOffset.UnixEpoch);

        Assert.That(first.ManifestJson, Is.EqualTo(second.ManifestJson));
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public void Build_NoFragments_StillProducesAReadableArchiveWithAManifest()
    {
        var result = DataExportArchive.Build("dxrq_3", "user_1", [], DateTimeOffset.UnixEpoch);

        var files = Unzip(result.Content);

        Assert.Multiple(() =>
        {
            Assert.That(files, Has.Count.EqualTo(1));
            Assert.That(files.Keys, Does.Contain("manifest.json"));
            Assert.That(result.TotalRows, Is.EqualTo(0));
        });
    }

    [Test]
    public void Build_FragmentThatIsNotValidJson_IsPreservedAsAParseableFile()
    {
        var result = DataExportArchive.Build("dxrq_4", "user_1",
            [Fragment("guild", "this is not json")], DateTimeOffset.UnixEpoch);

        var files = Unzip(result.Content);

        // Parses - Art. 20 asks for a machine-readable format, and one unreadable file makes the
        // whole archive suspect. The bad content is kept rather than dropped: it is still the only
        // answer that service gave.
        using var document = JsonDocument.Parse(files["guild.json"]);
        Assert.That(document.RootElement.GetProperty("raw").GetString(), Is.EqualTo("this is not json"));
    }

    [Test]
    public void Build_FailedFragment_IsRecordedInTheManifestRatherThanDropped()
    {
        var failed = Fragment("isle", "{}");
        failed.Error = "Isle was unreachable.";

        var result = DataExportArchive.Build("dxrq_5", "user_1", [failed], DateTimeOffset.UnixEpoch);

        using var manifest = JsonDocument.Parse(result.ManifestJson);
        var service = manifest.RootElement.GetProperty("services").EnumerateArray().Single();

        Assert.Multiple(() =>
        {
            Assert.That(service.GetProperty("service").GetString(), Is.EqualTo("isle"));
            Assert.That(service.GetProperty("error").GetString(), Is.EqualTo("Isle was unreachable."));
        });
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public void Build_ManifestStatesTheNoThirdPartyDataRuleInTheArchiveItself()
    {
        var result = DataExportArchive.Build("dxrq_6", "user_1", [], DateTimeOffset.UnixEpoch);

        using var manifest = JsonDocument.Parse(result.ManifestJson);
        var notice = manifest.RootElement.GetProperty("notice").GetString();

        // Somebody opening this file years from now should not have to infer the rule from what
        // happens to be absent.
        Assert.That(notice, Does.Contain("only"));
        Assert.That(notice, Does.Contain("email"));
    }
}
