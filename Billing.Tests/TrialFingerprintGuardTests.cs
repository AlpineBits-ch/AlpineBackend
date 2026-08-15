using System.Reflection;
using System.Text.Json;
using AppEnvironment;
using Billing.Application.Dtos;
using Billing.Application.Promotions;
using Billing.Application.Services;
using Billing.Application.Stripe;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Billing.Tests;

/// <summary>The card fingerprint does not leave this service, in any form, by any route.</summary>
[TestFixture]
public class TrialFingerprintGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Distinctive enough that finding it in a log or a response body is unambiguous.</summary>
    private const string Fingerprint = "fp_QQQ_unmistakable_card_identity_ZZZ";

    /// <summary>The four files allowed to name the identifier at all: the two halves of the Stripe
    /// seam that read it, the service that evaluates it as evidence, and the one that hands it
    /// over.</summary>
    private static readonly string[] AllowedFiles =
    [
        "IStripeGateway.cs",
        "PromotionEligibilityService.cs",
        "StripeGateway.cs",
        "TrialService.cs",
    ];

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    // ── The source guard ─────────────────────────────────────────────────────

    private static string ApplicationRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "Billing.Application", "Endpoints")))
        {
            directory = directory.Parent;
        }

        Assert.That(directory, Is.Not.Null, "could not locate Billing.Application in the source tree");

        return Path.Combine(directory!.FullName, "Billing.Application");
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(ApplicationRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>Only four files in the service so much as name the identifier.</summary>
    [Test]
    public void Only_the_stripe_seam_and_the_trial_path_name_a_fingerprint()
    {
        var offenders = SourceFiles()
            .Where(path => File.ReadAllText(path).Contains("Fingerprint", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.That(offenders, Is.EqualTo(AllowedFiles.OrderBy(name => name, StringComparer.Ordinal).ToList()),
            "a card fingerprint is an identity token shared by every account using the same physical "
            + "card - read StripeCardIdentity before adding a file to this list");
    }

    // ── The DTO guard ────────────────────────────────────────────────────────

    /// <summary>Nothing the client can be handed carries one.</summary>
    [Test]
    public void No_request_response_or_dto_type_carries_a_fingerprint()
    {
        var wireTypes = LoadableTypes()
            .Where(type => type.IsPublic)
            .Where(type => type.Name.EndsWith("Dto", StringComparison.Ordinal)
                           || type.Name.EndsWith("Request", StringComparison.Ordinal)
                           || type.Name.EndsWith("Response", StringComparison.Ordinal))
            .ToList();

        Assert.That(wireTypes, Is.Not.Empty, "reflection found no wire types, so this proves nothing");

        var offenders = wireTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.Name.Contains("Fingerprint", StringComparison.OrdinalIgnoreCase)
                                   || property.PropertyType == typeof(StripeCardIdentity))
                .Select(property => $"{type.Name}.{property.Name}"))
            .ToList();

        Assert.That(offenders, Is.Empty,
            "a fingerprint on a wire type turns the anti-abuse control into an oracle anybody can query");
    }

    /// <summary>The one type it would have been easiest to add it to, named on its own.</summary>
    [Test]
    public void The_card_summary_and_its_dto_have_no_fingerprint_member()
    {
        Assert.Multiple(() =>
        {
            foreach (var type in new[] { typeof(StripePaymentMethodSummary), typeof(PaymentMethodDto) })
            {
                Assert.That(
                    type.GetProperties().Select(property => property.Name),
                    Has.None.Contains("Fingerprint"),
                    $"{type.Name} is served to a client and must never carry a card's identity");
            }
        });
    }

    private static IEnumerable<Type> LoadableTypes()
    {
        try
        {
            return typeof(TrialService).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            // A Wolverine-generated type that will not load is not what is under test here.
            return partial.Types.Where(type => type is not null).Select(type => type!);
        }
    }

    // ── The behavioural guard ────────────────────────────────────────────────

    /// <summary>
    /// A real trial start, with a fingerprint chosen so it cannot be mistaken for anything else,
    /// and then a search for it in every place it could have come to rest.
    /// </summary>
    [Test]
    public async Task A_started_trial_leaves_the_fingerprint_in_no_log_no_response_and_no_row()
    {
        await PostgresTestDatabase.ResetToEmptyAsync();

        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        var originalSecretKey = Env.License.StripeSecretKey;
        var originalMode = Env.License.Mode;
        Env.License.StripeSecretKey = PromotionFixtures.StripeSecretKey;
        Env.License.Mode = LicenseConfiguration.Hosted;

        try
        {
            var clock = new TestClock(Now);
            var log = new RecordingLoggerFactory();
            var gateway = PromotionFixtures.StripeSaying(Fingerprint);
            var bus = PromotionFixtures.AllowingGuild(PromotionFixtures.IdentitySaying());

            await PromotionFixtures.SeedPlansAsync(db, Now);

            var cards = new PaymentMethodService(
                db, gateway, new StripeCustomerRegistry(db, gateway));

            await cards.CreateSetupIntentAsync(PromotionFixtures.Owner, CancellationToken.None);

            var campaigns = PromotionFixtures.Campaigns(db, clock);

            var campaign = await campaigns.CreateAsync(
                PromotionFixtures.Open(rules: ["payment_card"]), PromotionFixtures.Staff,
                CancellationToken.None);

            await db.SaveChangesAsync();

            var started = await PromotionFixtures
                .Trials(db, clock, gateway, bus, campaigns, loggers: log)
                .StartAsync(
                    campaign.Code, PromotionFixtures.Guild, PromotionFixtures.PaymentMethodId,
                    PromotionFixtures.Owner, CancellationToken.None);

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var response = JsonSerializer.Serialize(
                new StartTrialResponse(
                    campaign.Code, campaign.Plan, started.Owner.EndsAt, started.Subscription,
                    started.ClientSecret),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var marks = await db.PromotionIdentityMarks.Select(mark => mark.Hash).ToListAsync();

            Assert.Multiple(() =>
            {
                Assert.That(log.Lines, Is.Not.Empty,
                    "nothing was logged at all, so finding no fingerprint proves nothing");

                Assert.That(log.Lines.Any(line => line.Contains(Fingerprint, StringComparison.Ordinal)),
                    Is.False,
                    "a fingerprint in a log line is a cross-account correlation key in whatever "
                    + "aggregates logs");

                Assert.That(response, Does.Not.Contain(Fingerprint));
                Assert.That(marks, Has.None.Contains(Fingerprint));
                Assert.That(marks, Is.Not.Empty, "no marks were written, so this proves nothing either");
            });
        }
        finally
        {
            Env.License.StripeSecretKey = originalSecretKey;
            Env.License.Mode = originalMode;
        }
    }

    /// <summary>Collects every line the promotion path writes, so the assertion above can be made
    /// about what was logged rather than about what the source looks like.</summary>
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Recorder(Lines);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Recorder(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                lines.Add(formatter(state, exception));
            }
        }
    }
}
