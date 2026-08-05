using System.Globalization;
using System.Text;
using AppEnvironment;
using Identity.Application.Services;
using Identity.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace Identity.Tests.Services;

/// <summary>
/// T1-12. The catalog is what makes "a silent edit is detectable" true: it hashes the bytes actually
/// served, every startup, and the manifest - not the directory listing - decides what exists.
/// </summary>
[TestFixture]
public class LegalDocumentCatalogTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "echo-legal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private LegalDocumentCatalog NewCatalog() => new(NullLogger<LegalDocumentCatalog>.Instance)
    {
        DirectoryPath = _dir,
        PublicBaseUrl = "https://example.test/api/v1/identity/legal/documents",
    };

    private void WriteManifest(string json) =>
        File.WriteAllText(Path.Combine(_dir, "manifest.json"), json);

    private void WriteDocument(string name, string content) =>
        File.WriteAllText(Path.Combine(_dir, name), content);

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public void Load_ReturnsEveryDeclaredDocument_HashedFromTheBytesOnDisk()
    {
        WriteDocument("terms-1.0.0.md", "# Terms\n");
        WriteManifest("""
            {"documents":[
              {"documentType":"Terms","version":"1.0.0","effectiveAt":"2026-01-01T00:00:00Z","file":"terms-1.0.0.md"}
            ]}
            """);

        var loaded = NewCatalog().Load();

        Assert.That(loaded, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(loaded[0].DocumentType, Is.EqualTo(LegalDocumentType.Terms));
            Assert.That(loaded[0].Version, Is.EqualTo("1.0.0"));
            Assert.That(loaded[0].ContentHash,
                Is.EqualTo(LegalDocumentCatalog.HashOf(Encoding.UTF8.GetBytes("# Terms\n"))));
        });
    }

    [Test]
    public void Load_AfterAnEditToAPublishedFile_ProducesADifferentHash()
    {
        // The whole reason the hash is stored.
        WriteDocument("privacy-1.0.0.md", "original");
        WriteManifest("""
            {"documents":[
              {"documentType":"Privacy","version":"1.0.0","effectiveAt":"2026-01-01T00:00:00Z","file":"privacy-1.0.0.md"}
            ]}
            """);

        var before = NewCatalog().Load()[0].ContentHash;
        WriteDocument("privacy-1.0.0.md", "quietly changed");
        var after = NewCatalog().Load()[0].ContentHash;

        Assert.That(after, Is.Not.EqualTo(before));
    }

    [Test]
    public void ReadContent_ReturnsExactlyTheBytesThatWereHashed()
    {
        WriteDocument("cookies-2.0.md", "# Cookies\r\nline two\n");
        WriteManifest("""
            {"documents":[
              {"documentType":"Cookies","version":"2.0","effectiveAt":"2026-01-01T00:00:00Z","file":"cookies-2.0.md"}
            ]}
            """);

        var catalog = NewCatalog();
        var declared = catalog.Load()[0];
        var served = catalog.ReadContent(LegalDocumentType.Cookies, "2.0");

        Assert.That(served, Is.Not.Null);
        Assert.That(LegalDocumentCatalog.HashOf(served!), Is.EqualTo(declared.ContentHash),
            "an auditor has to be able to fetch the URL, hash it, and get the value in the row");
    }

    [Test]
    public void UrlFor_BuildsThePublicAddressTheRowCarries()
    {
        Assert.That(NewCatalog().UrlFor(LegalDocumentType.Privacy, "1.0.0"),
            Is.EqualTo("https://example.test/api/v1/identity/legal/documents/privacy/1.0.0"));
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public void Load_WithNoManifest_IsEmptyRatherThanAFailure()
    {
        // A deployment that has not mounted the documents yet should be diagnosable, not
        // crash-looping. An empty catalog demands nothing of any account.
        Assert.That(NewCatalog().Load(), Is.Empty);
    }

    [Test]
    public void Load_WithAMalformedManifest_IsEmptyRatherThanAThrow()
    {
        WriteManifest("{ not json");

        Assert.That(NewCatalog().Load(), Is.Empty);
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public void Load_SkipsAFileThatIsPresentButNotDeclared()
    {
        // The manifest is the source of truth.
        WriteDocument("terms-1.0.0.md", "declared");
        WriteDocument("terms-2.0.0-draft.md", "NOT READY");
        WriteManifest("""
            {"documents":[
              {"documentType":"Terms","version":"1.0.0","effectiveAt":"2026-01-01T00:00:00Z","file":"terms-1.0.0.md"}
            ]}
            """);

        var loaded = NewCatalog().Load();

        Assert.That(loaded.Select(d => d.Version), Is.EquivalentTo(new[] { "1.0.0" }));
    }

    [Test]
    public void Load_SkipsAnEntryNamingAMissingFile()
    {
        WriteManifest("""
            {"documents":[
              {"documentType":"Terms","version":"1.0.0","effectiveAt":"2026-01-01T00:00:00Z","file":"gone.md"}
            ]}
            """);

        Assert.That(NewCatalog().Load(), Is.Empty);
    }

    [Test]
    public void Load_SkipsAnEntryWithAnUnknownDocumentType()
    {
        WriteDocument("something.md", "x");
        WriteManifest("""
            {"documents":[
              {"documentType":"Marketing","version":"1.0.0","effectiveAt":"2026-01-01T00:00:00Z","file":"something.md"}
            ]}
            """);

        Assert.That(NewCatalog().Load(), Is.Empty);
    }

    [TestCase("../secrets.md")]
    [TestCase("..\\secrets.md")]
    [TestCase("sub/dir.md")]
    public void Load_RefusesAManifestEntryThatEscapesTheDirectory(string file)
    {
        // The value ends up in a File.ReadAllBytes and in a route.
        WriteManifest($$"""
            {"documents":[
              {"documentType":"Terms","version":"1.0.0","effectiveAt":"2026-01-01T00:00:00Z","file":"{{file}}"}
            ]}
            """);

        Assert.That(NewCatalog().Load(), Is.Empty);
    }

    [Test]
    public void ReadContent_ForAnUndeclaredVersion_IsNull()
    {
        WriteDocument("terms-1.0.0.md", "declared");
        WriteManifest("""
            {"documents":[
              {"documentType":"Terms","version":"1.0.0","effectiveAt":"2026-01-01T00:00:00Z","file":"terms-1.0.0.md"}
            ]}
            """);

        Assert.That(NewCatalog().ReadContent(LegalDocumentType.Terms, "9.9.9"), Is.Null);
    }

    // ── the documents this build actually ships ─────────────────────────────

    [Test]
    public void TheShippedDocuments_LoadAndAreTheRealPublishedText()
    {
        // docs/legal is copied next to the binary by Identity.Application.csproj.
        var catalog = new LegalDocumentCatalog(NullLogger<LegalDocumentCatalog>.Instance)
        {
            DirectoryPath = Env.Legal.DirectoryPath,
            PublicBaseUrl = Env.Legal.PublicBaseUrl,
        };

        var loaded = catalog.Load();

        // Distinct, not the raw list.
        Assert.That(loaded.Select(d => d.DocumentType).Distinct(), Is.EquivalentTo(new[]
        {
            LegalDocumentType.Terms, LegalDocumentType.Privacy, LegalDocumentType.Cookies,
        }), "the manifest in docs/legal must reach the build output");

        // Superseded versions have to keep loading, because every stored consent points at the
        // exact version it was given - a historical document that stops loading turns those records
        // into references to text nobody can produce.
        Assert.That(loaded.Select(d => (d.DocumentType, d.Version)), Is.Unique,
            "a (type, version) pair must appear once - two entries for one version would make "
            + "ContentHash ambiguous");

        Assert.Multiple(() =>
        {
            foreach (var document in loaded)
            {
                var content = Encoding.UTF8.GetString(
                    catalog.ReadContent(document.DocumentType, document.Version)!);

                // This assertion used to run the other way round: it required the banner, because
                // what shipped then was a generated outline and a plausible-looking generated
                // policy is worse than an obvious placeholder.
                Assert.That(content, Does.Not.Contain("LEGAL REVIEW REQUIRED"),
                    $"{document.DocumentType} v{document.Version} is still an unreviewed "
                    + "placeholder and must not ship");

                // The version IS the document's own "last updated" date.
                Assert.That(content, Does.Contain(ExpectedLastUpdated(document.Version)),
                    $"{document.DocumentType} v{document.Version} does not carry a matching "
                    + $"'Last updated' line - either the text was edited without bumping the "
                    + "version, or the manifest version is wrong");
            }
        });
    }

    /// <summary>Renders a <c>yyyy-MM-dd</c> manifest version as the "Last updated: August 4, 2026"
    /// form the documents themselves use.</summary>
    private static string ExpectedLastUpdated(string version)
    {
        var date = DateOnly.ParseExact(version, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"Last updated: {date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)}";
    }
}
