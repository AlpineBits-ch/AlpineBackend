using System.Text.RegularExpressions;

namespace Echo.Tests.Sites;

/// <summary>Pins the asset paths the two hand-written sites reference.</summary>
[TestFixture]
[Category("Unit")]
public class SiteAssetPathTests
{
    /// <summary>Walks up from the test binary to the repo root.</summary>
    private static string WebRoot
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Echo", "wwwroot")))
            {
                directory = directory.Parent;
            }

            Assert.That(directory, Is.Not.Null, "could not locate Echo/wwwroot from the test directory");
            return Path.Combine(directory!.FullName, "Echo", "wwwroot");
        }
    }

    private static string Page(string site) => File.ReadAllText(Path.Combine(WebRoot, site, "index.html"));

    /// <summary>Every local href/src in a page, excluding absolute URLs and fragments.</summary>
    private static IEnumerable<string> LocalReferences(string html) =>
        Regex.Matches(html, "(?:href|src)\\s*=\\s*\"(/[^\"]*)\"")
            .Select(m => m.Groups[1].Value);

    /// <summary>The subset that names a file on disk.</summary>
    private static IEnumerable<string> AssetReferences(string html) =>
        LocalReferences(html).Where(r => Path.HasExtension(r.Split('?')[0]));

    [TestCase("admin")]
    [TestCase("support")]
    public void Every_referenced_asset_resolves_to_a_file_that_exists(string site)
    {
        var references = AssetReferences(Page(site)).Distinct().ToList();

        Assert.That(references, Is.Not.Empty, "the page must reference its stylesheet and script");

        Assert.Multiple(() =>
        {
            foreach (var reference in references)
            {
                // /assets/** is the shared folder, mounted at that request path on both hosts.
                var path = reference.StartsWith("/assets/", StringComparison.Ordinal)
                    ? Path.Combine(WebRoot, reference.TrimStart('/').Replace('/', Path.DirectorySeparatorChar))
                    : Path.Combine(WebRoot, site, reference.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

                Assert.That(File.Exists(path), Is.True,
                    $"{site}/index.html references {reference}, which resolves to {path} - nothing is there");
            }
        });
    }

    /// <summary>The specific mistake, named.</summary>
    [TestCase("admin")]
    [TestCase("support")]
    public void No_reference_repeats_the_site_folder_in_its_path(string site)
    {
        var offenders = LocalReferences(Page(site))
            .Where(r => r.StartsWith($"/{site}/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.That(offenders, Is.Empty,
            $"{site}/ is the site root on its own host, so these resolve to /{site}/{site}/...");
    }

    /// <summary>
    /// Both sites must reach the shared icon set and stylesheet, which is the one thing they are
    /// <em>not</em> allowed to reference relatively.
    /// </summary>
    [TestCase("admin")]
    [TestCase("support")]
    public void Each_page_loads_the_shared_stylesheet_and_icon_injector(string site)
    {
        var html = Page(site);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("/assets/venta.css"));
            Assert.That(html, Does.Contain("/assets/icons.js"));
        });
    }

    /// <summary>Every icon the pages ask for by name has a file behind it.</summary>
    [Test]
    public void Every_named_icon_has_a_file()
    {
        var iconDirectory = Path.Combine(WebRoot, "assets", "icons");

        var available = Directory.GetFiles(iconDirectory, "*.svg")
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sources = new[] { "admin", "support" }
            .SelectMany(site => Directory.GetFiles(Path.Combine(WebRoot, site)))
            .Select(File.ReadAllText);

        // data-icon="name" in the markup, and dataset.icon = 'name' / icon('name') in the scripts.
        var named = sources
            .SelectMany(text => Regex.Matches(text, """(?:data-icon="|dataset\.icon\s*=\s*'|\bicon\(')([a-z0-9-]+)""")
                .Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        Assert.That(named, Is.Not.Empty, "the pages must name some icons");

        var missing = named.Where(name => !available.Contains(name)).ToList();

        Assert.That(missing, Is.Empty,
            "these icons are referenced but have no file in wwwroot/assets/icons, so they render as "
            + "nothing at all: " + string.Join(", ", missing));
    }
}
