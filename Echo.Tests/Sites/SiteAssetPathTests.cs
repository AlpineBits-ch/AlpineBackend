using System.Reflection;
using System.Text.RegularExpressions;
using Echo.Domain.Entities.Moderation;
using Echo.Domain.Enums;

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

    /// <summary>
    /// Scopes Identity will accept: the two protocol scopes, plus the three it registers.
    /// </summary>
    private static readonly string[] AcceptedScopes =
        ["openid", "offline_access", "email", "profile", "roles"];

    /// <summary>
    /// The console may only ask for scopes the authorization server actually has.
    /// </summary>
    [Test]
    public void The_console_requests_only_scopes_the_server_accepts()
    {
        var script = File.ReadAllText(Path.Combine(WebRoot, "admin", "app.js"));

        var requested = Regex.Matches(script, @"scope:\s*'([^']*)'")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.That(requested, Is.Not.Empty, "the sign-in form must request a scope");

        Assert.Multiple(() =>
        {
            foreach (var scope in requested)
            {
                var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                Assert.That(scopes, Is.SubsetOf(AcceptedScopes),
                    "an unregistered scope is refused with invalid_scope before the client's own "
                    + "permissions are consulted, so the sign-in fails outright");

                Assert.That(scopes, Does.Contain("offline_access"),
                    "without it there is no refresh token, and the console's session dies at the "
                    + "first access-token expiry mid-triage");
            }
        });
    }

    /// <summary>The page scripts actually parse.</summary>
    [TestCase("admin/app.js")]
    [TestCase("support/app.js")]
    [TestCase("assets/icons.js")]
    public void Every_page_script_parses(string relativePath)
    {
        var script = Path.Combine(WebRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.That(File.Exists(script), Is.True, $"{relativePath} is missing");

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo("node", $"--check \"{script}\"")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            Assert.Ignore($"node is not on PATH, so {relativePath} could not be parse-checked: {ex.Message}");
            return;
        }

        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(milliseconds: 30_000);

        Assert.That(process.ExitCode, Is.Zero, $"{relativePath} does not parse:\n{stderr}");
    }

    /// <summary>
    /// The support form's categories are real <see cref="SupportTicketCategory"/> names.
    /// </summary>
    [Test]
    public void The_support_form_offers_only_real_ticket_categories()
    {
        var html = Page("support");

        var offered = Regex.Matches(html, "<option value=\"([A-Za-z]+)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.That(offered, Is.Not.Empty, "the contact form must offer categories");

        Assert.That(offered, Is.SubsetOf(Enum.GetNames<SupportTicketCategory>()),
            "an option whose value is not a SupportTicketCategory member is a 400 on submit");
    }

    /// <summary>The console's reason list covers <see cref="ReportReason"/> exactly.</summary>
    [Test]
    public void The_console_offers_every_report_reason()
    {
        var script = File.ReadAllText(Path.Combine(WebRoot, "admin", "app.js"));

        // The REASONS table: ['Spam', 'Spam or unsolicited advertising'], one per line.
        var listed = Regex.Matches(script, @"\['([A-Za-z]+)',\s*'[^']+'\]")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        var reasons = Enum.GetNames<ReportReason>();

        Assert.That(listed.Intersect(reasons), Is.EquivalentTo(reasons),
            "every ReportReason must be offered, or it can never be chosen");
    }

    /// <summary>Every audited action has a label in the console.</summary>
    [Test]
    public void Every_audited_action_has_a_label_in_the_console()
    {
        var script = File.ReadAllText(Path.Combine(WebRoot, "admin", "app.js"));

        var labelled = Regex.Matches(script, @"'([a-z]+\.[a-z-]+)':\s*'")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        // The constants, read off the type rather than copied - a list duplicated here would be the
        // same maintenance problem one level further out.
        var audited = typeof(ModerationAuditActions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) })
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.That(audited, Is.Not.Empty, "the audit action constants could not be read");

        var missing = audited.Where(action => !labelled.Contains(action)).ToList();

        Assert.That(missing, Is.Empty,
            "these are written to the audit log but have no label in AUDIT_VERBS, so they render as "
            + "their raw dotted name: " + string.Join(", ", missing));
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
