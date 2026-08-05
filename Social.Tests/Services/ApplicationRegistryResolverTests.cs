using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Api.Services;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Services;

/// <summary>Learning the name of an application the bootstrap catalog never contained.</summary>
[TestFixture]
public class ApplicationRegistryResolverTests
{
    private const string VolantaId = "1293582351376584824";
    private const string KnownId = "356875221078245376";

    private TestSocialContext _context = null!;
    private FakeDistributedCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestSocialContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private ApplicationRegistryResolver Resolver(StubHttpClientFactory http) =>
        new(_context, http, _cache, NullLogger<ApplicationRegistryResolver>.Instance);

    [Test]
    public async Task An_unknown_application_is_resolved_and_stored()
    {
        var http = StubHttpClientFactory.Returning("Volanta");

        var name = await Resolver(http).ResolveAndStoreAsync(VolantaId);

        Assert.That(name, Is.EqualTo("Volanta"));
        Assert.That(http.Requests, Is.EqualTo(new[] { $"/api/v9/applications/{VolantaId}/rpc" }));

        var stored = _context.GameApplications.Single(g => g.DiscordApplicationId == VolantaId);
        Assert.Multiple(() =>
        {
            Assert.That(stored.Name, Is.EqualTo("Volanta"));
            // Never Seeded: the seeder is allowed to overwrite those, and this row did not come
            // from the bootstrap artifact.
            Assert.That(stored.Source, Is.EqualTo(GameCatalogSource.Resolved));
            Assert.That(stored.IsEnabled, Is.True);
            // No executables - this row answers "what is this id called", it is not detectable.
            Assert.That(stored.Executables, Is.Empty);
        });
    }

    /// <summary>The economics of the whole design: one call per id ever, not one per write.</summary>
    [Test]
    public async Task A_second_sighting_costs_no_request()
    {
        var http = StubHttpClientFactory.Returning("Volanta");
        var resolver = Resolver(http);

        await resolver.ResolveAndStoreAsync(VolantaId);
        var again = await resolver.ResolveAndStoreAsync(VolantaId);

        Assert.That(again, Is.EqualTo("Volanta"));
        Assert.That(http.CallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task An_application_already_in_the_catalog_is_never_looked_up()
    {
        _context.GameApplications.Add(new GameApplication
        {
            Id = GameApplication.GenerateId(),
            DiscordApplicationId = KnownId,
            Name = "Overwatch",
            Source = GameCatalogSource.Seeded,
            IsEnabled = true,
        });
        await _context.SaveChangesAsync();

        var http = StubHttpClientFactory.Returning("Something Else");

        Assert.That(await Resolver(http).ResolveAndStoreAsync(KnownId), Is.EqualTo("Overwatch"));
        Assert.That(http.CallCount, Is.Zero, "a catalog hit must not reach the network");
    }

    /// <summary>
    /// The abuse case the negative cache exists for: a local process can present any id it likes, so
    /// invented ids must not turn activity writes into an outbound request generator.
    /// </summary>
    [Test]
    public async Task An_unresolvable_id_is_only_looked_up_once()
    {
        var http = StubHttpClientFactory.NotFound();
        var resolver = Resolver(http);

        Assert.That(await resolver.ResolveAndStoreAsync(VolantaId), Is.Null);
        Assert.That(await resolver.ResolveAndStoreAsync(VolantaId), Is.Null);
        Assert.That(await resolver.ResolveAndStoreAsync(VolantaId), Is.Null);

        Assert.That(http.CallCount, Is.EqualTo(1));
        Assert.That(_context.GameApplications.Any(), Is.False, "nothing may be stored for an id that does not resolve");
    }

    [Test]
    public async Task A_failing_registry_yields_null_rather_than_throwing()
    {
        Assert.That(await Resolver(StubHttpClientFactory.Failing(HttpStatusCode.InternalServerError))
            .ResolveAndStoreAsync(VolantaId), Is.Null);

        _cache = new FakeDistributedCache();
        _context = new TestSocialContext(Guid.NewGuid().ToString());

        Assert.That(await Resolver(StubHttpClientFactory.Unreachable())
            .ResolveAndStoreAsync(VolantaId), Is.Null);
    }

    /// <summary>A malformed id must not reach the network - it cannot name anything, and the id is
    /// interpolated into a URL path.</summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not-a-snowflake")]
    [TestCase("../../admin")]
    [TestCase("123456789012345678901")]
    public async Task A_malformed_id_is_rejected_without_a_request(string? applicationId)
    {
        var http = StubHttpClientFactory.Returning("Whatever");

        Assert.That(await Resolver(http).ResolveAndStoreAsync(applicationId), Is.Null);
        Assert.That(http.CallCount, Is.Zero);
    }

    /// <summary>The registry's answer is still cleaned.</summary>
    [Test]
    public async Task A_hostile_registry_name_is_sanitized()
    {
        var http = StubHttpClientFactory.Returning("Evil\u202eGame\0\n\nName");

        var name = await Resolver(http).ResolveAndStoreAsync(VolantaId);

        Assert.That(name, Is.Not.Null);
        Assert.That(name, Does.Not.Contain('\u202e'), "bidi override survives HTML escaping and must be stripped");
        Assert.That(name, Does.Not.Contain('\0'));
        Assert.That(name, Does.Not.Contain('\n'));
    }

    [Test]
    public async Task An_empty_name_is_treated_as_unresolved()
    {
        var http = StubHttpClientFactory.Returning("   ");

        Assert.That(await Resolver(http).ResolveAndStoreAsync(VolantaId), Is.Null);
        Assert.That(_context.GameApplications.Any(), Is.False);
    }
}
