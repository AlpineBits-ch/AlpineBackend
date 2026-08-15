using System.Reflection;
using Identity.Application.Templates;
using RazorLight;

namespace Identity.Tests.Services;

/// <summary>The three account mails, actually rendered.</summary>
[TestFixture]
[Category("Unit")]
public class EmailTemplateTests
{
    private RazorLightEngine _engine = null!;

    /// <summary>
    /// Points RazorLight at the templates in the source tree rather than at the build output.
    /// </summary>
    [OneTimeSetUp]
    public void SetUp()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "Identity.Application", "Templates")))
        {
            directory = directory.Parent;
        }

        Assert.That(directory, Is.Not.Null, "could not locate Identity.Application/Templates");

        _engine = new RazorLightEngineBuilder()
            .UseFileSystemProject(Path.Combine(directory!.FullName, "Identity.Application", "Templates"))
            .UseMemoryCachingProvider()
            .Build();
    }

    private Task<string> Render<T>(string template, T model) =>
        _engine.CompileRenderAsync(template, model);

    // ── They compile, and they say what they should ─────────────────────────

    [Test]
    public async Task WelcomeEmail_RendersTheNameAndTheCode()
    {
        var html = await Render("WelcomeEmail.cshtml", new WelcomeEmail
        {
            Name = "Sam", Email = "sam@example.com", ConfirmationCode = "482913",
        });

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Sam"));
            Assert.That(html, Does.Contain("sam@example.com"));
            Assert.That(html, Does.Contain("482913"), "the code is the entire point of this mail");
        });
    }

    [Test]
    public async Task PasswordResetEmail_RendersTheCode()
    {
        var html = await Render("PasswordResetEmail.cshtml", new PasswordResetEmail
        {
            Name = "Sam", Email = "sam@example.com", ResetCode = "551204",
        });

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("551204"));
            Assert.That(html, Does.Contain("sam@example.com"));
        });
    }

    [Test]
    public async Task RegistrationAttemptEmail_RendersTheAccountHoldersOwnDetails()
    {
        var html = await Render("RegistrationAttemptEmail.cshtml", new RegistrationAttemptEmail
        {
            Name = "Sam", Email = "sam@example.com",
        });

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Sam"));
            Assert.That(html, Does.Contain("sam@example.com"));
        });
    }

    /// <summary>The sign-up-attempt mail can only ever say things about the recipient.</summary>
    [Test]
    public void RegistrationAttemptEmail_ExposesOnlyTheRecipientsOwnFields()
    {
        var properties = typeof(RegistrationAttemptEmail)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .ToList();

        Assert.That(properties, Is.EquivalentTo(new[] { "Name", "Email" }),
            "a field added here is a field an anonymous caller may be able to put in front of "
            + "somebody else - read the model's remarks before adding one");
    }

    // ── The rendering rules ─────────────────────────────────────────────────

    private static readonly object[] Templates =
    [
        new object[] { "WelcomeEmail.cshtml", (object)new WelcomeEmail { Name = "Sam", Email = "s@e.co", ConfirmationCode = "123456" } },
        new object[] { "PasswordResetEmail.cshtml", (object)new PasswordResetEmail { Name = "Sam", Email = "s@e.co", ResetCode = "123456" } },
        new object[] { "RegistrationAttemptEmail.cshtml", (object)new RegistrationAttemptEmail { Name = "Sam", Email = "s@e.co" } },

        // The three billing mails.
        new object[] { "CreditIssuedEmail.cshtml", (object)new CreditIssuedEmail { Name = "Sam", Email = "s@e.co", Points = "1,500", BalancePoints = "2,500", ExpiresOn = "14 November 2026", Disclaimer = "Credits have no cash value." } },
        new object[] { "EntitlementGrantEmail.cshtml", (object)new EntitlementGrantEmail { Name = "Sam", Email = "s@e.co", Headline = "You now have Pro", Summary = "we have added Pro to your account.", PlanDisplayName = "Pro", ExpiresOn = "14 November 2026" } },
        new object[] { "PlanUpgradedEmail.cshtml", (object)new PlanUpgradedEmail { Name = "Sam", Email = "s@e.co", PlanDisplayName = "Pro", PreviousPlanDisplayName = "Plus", RenewsOn = "3 September 2026" } },
    ];

    /// <summary>The rule these mails broke: a stylesheet a client strips takes the whole design with
    /// it, and the failure is silent - the mail arrives, says the right words, and is unreadable.</summary>
    [TestCaseSource(nameof(Templates))]
    public async Task No_template_depends_on_a_style_block(string template, object model)
    {
        var html = await Render(template, model);

        Assert.That(html, Does.Not.Contain("<style"),
            "every declaration must be inline - Gmail drops rules attached to <body> and Outlook "
            + "renders through Word");
    }

    [TestCaseSource(nameof(Templates))]
    public async Task Every_template_sets_its_background_with_an_attribute(string template, object model)
    {
        var html = await Render(template, model);

        Assert.That(html, Does.Contain("bgcolor="),
            "a background set only in CSS on <body> is one Gmail and Outlook will drop");
    }

    [TestCaseSource(nameof(Templates))]
    public async Task No_template_uses_css_outlook_ignores(string template, object model)
    {
        var html = await Render(template, model);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("linear-gradient"), "Outlook renders it as nothing");
            Assert.That(html, Does.Not.Contain("display:flex"), "Outlook collapses it");
        });
    }

    /// <summary>Declared both ways, which is what stops the better-behaved clients auto-inverting a
    /// light design into a low-contrast dark one.</summary>
    [TestCaseSource(nameof(Templates))]
    public async Task Every_template_declares_a_light_colour_scheme(string template, object model)
    {
        var html = await Render(template, model);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("name=\"color-scheme\""));
            Assert.That(html, Does.Contain("name=\"supported-color-schemes\""));
        });
    }

    /// <summary>None of the old dark palette survives anywhere.</summary>
    [TestCaseSource(nameof(Templates))]
    public async Task No_template_keeps_a_colour_from_the_old_dark_palette(string template, object model)
    {
        var html = await Render(template, model);

        string[] retired = ["#111318", "#1a1d26", "#e2e4ea", "#8b8fa8", "#5a5e72", "#454858", "#3a3d50", "#c8cbda", "#22263a", "#2a2d3a"];

        Assert.Multiple(() =>
        {
            foreach (var colour in retired)
            {
                Assert.That(html, Does.Not.Contain(colour).IgnoreCase,
                    $"{colour} is from the dark design these were rewritten away from");
            }
        });
    }

    /// <summary>The inbox preview line.</summary>
    [TestCaseSource(nameof(Templates))]
    public async Task Every_template_carries_a_preheader(string template, object model)
    {
        var html = await Render(template, model);

        Assert.That(html, Does.Contain("mso-hide:all"));
    }

    /// <summary>Model values reach the reader as text.</summary>
    [Test]
    public async Task A_display_name_containing_markup_is_escaped()
    {
        var html = await Render("WelcomeEmail.cshtml", new WelcomeEmail
        {
            Name = "<script>alert(1)</script>",
            Email = "s@e.co",
            ConfirmationCode = "123456",
        });

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("<script>"));
            Assert.That(html, Does.Contain("&lt;script&gt;"));
        });
    }
}
