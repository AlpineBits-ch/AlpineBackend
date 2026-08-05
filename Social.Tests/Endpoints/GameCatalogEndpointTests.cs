using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Social.Api.Endpoints;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Endpoints;

[TestFixture]
public class GameCatalogEndpointTests
{
    private TestSocialContext _context = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestSocialContext(Guid.NewGuid().ToString());

        _context.GameApplications.AddRange(
            Game("1", "World of Warcraft", enabled: true,
                Rule("_retail_/wow.exe", GamePlatform.Win32),
                Rule("wow.app", GamePlatform.Darwin)),
            Game("2", "Garry's Mod", enabled: true,
                Rule("win64/gmod.exe", GamePlatform.Win32),
                Rule("hl2.exe", GamePlatform.Win32, negated: true)),
            Game("3", "Switched Off", enabled: false,
                Rule("off.exe", GamePlatform.Win32)));

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static GameApplication Game(string appId, string name, bool enabled, params GameExecutable[] rules)
    {
        var game = new GameApplication
        {
            Id = GameApplication.GenerateId(),
            DiscordApplicationId = appId,
            Name = name,
            Source = GameCatalogSource.Seeded,
            IsEnabled = enabled,
        };

        foreach (var rule in rules)
        {
            rule.GameApplicationId = game.Id;
            game.Executables.Add(rule);
        }

        return game;
    }

    private static GameExecutable Rule(string name, GamePlatform os, bool launcher = false, bool negated = false)
    {
        var slash = name.LastIndexOf('/');
        return new GameExecutable
        {
            Id = GameExecutable.GenerateId(),
            Name = name,
            Basename = slash >= 0 ? name[(slash + 1)..] : name,
            Os = os,
            IsLauncher = launcher,
            IsNegated = negated,
        };
    }

    private static DefaultHttpContext Http(string? ifNoneMatch = null, string? acceptEncoding = null)
    {
        var http = new DefaultHttpContext();
        if (ifNoneMatch is not null) http.Request.Headers.IfNoneMatch = ifNoneMatch;
        if (acceptEncoding is not null) http.Request.Headers.AcceptEncoding = acceptEncoding;
        return http;
    }

    private static CatalogDto Deserialize(FileContentHttpResult result, bool gzipped = false)
    {
        var bytes = result.FileContents.ToArray();

        if (gzipped)
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            bytes = output.ToArray();
        }

        return JsonSerializer.Deserialize<CatalogDto>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    // ── Normal ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetCatalogAsync_ReturnsEnabledGamesWithTheirRules()
    {
        var http = Http();

        var result = await GameCatalogEndpoint.GetCatalogAsync(http, _context) as FileContentHttpResult;
        Assert.That(result, Is.Not.Null);

        var catalog = Deserialize(result!);

        Assert.That(catalog.Games, Has.Count.EqualTo(2), "the disabled application must not be served");
        Assert.That(catalog.GameCount, Is.EqualTo(2));
        Assert.That(catalog.Games.Select(g => g.Name), Is.EquivalentTo(new[] { "World of Warcraft", "Garry's Mod" }));
    }

    [Test]
    public async Task GetCatalogAsync_PreservesRuleShapeTheMatcherDependsOn()
    {
        var result = await GameCatalogEndpoint.GetCatalogAsync(Http(), _context) as FileContentHttpResult;
        var catalog = Deserialize(result!);

        var gmod = catalog.Games.Single(g => g.Name == "Garry's Mod");

        Assert.That(gmod.ApplicationId, Is.EqualTo("2"));
        Assert.That(gmod.Rules.Single(r => r.Name == "win64/gmod.exe").IsNegated, Is.False);
        Assert.That(gmod.Rules.Single(r => r.Name == "hl2.exe").IsNegated, Is.True,
            "without the negation the 34-way hl2.exe collision has no resolution");

        var wow = catalog.Games.Single(g => g.Name == "World of Warcraft");
        Assert.That(wow.Rules.Select(r => r.Os), Is.EquivalentTo(new[] { "win32", "darwin" }));
    }

    [Test]
    public async Task GetCatalogAsync_OsFilter_NarrowsToThatPlatform()
    {
        var result = await GameCatalogEndpoint.GetCatalogAsync(Http(), _context, os: "darwin") as FileContentHttpResult;
        var catalog = Deserialize(result!);

        Assert.That(catalog.Games, Has.Count.EqualTo(1));
        Assert.That(catalog.Games[0].Rules.Select(r => r.Name), Is.EquivalentTo(new[] { "wow.app" }));
    }

    // ── Caching ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetCatalogAsync_SetsETagAndRevalidationHeaders()
    {
        var http = Http();

        await GameCatalogEndpoint.GetCatalogAsync(http, _context);

        Assert.That(http.Response.Headers.ETag.ToString(), Is.Not.Empty);
        Assert.That(http.Response.Headers.CacheControl.ToString(), Does.Contain("no-cache"));
    }

    [Test]
    public async Task GetCatalogAsync_MatchingIfNoneMatch_Returns304()
    {
        var first = Http();
        await GameCatalogEndpoint.GetCatalogAsync(first, _context);
        var etag = first.Response.Headers.ETag.ToString();

        var result = await GameCatalogEndpoint.GetCatalogAsync(Http(ifNoneMatch: etag), _context);

        Assert.That(result, Is.TypeOf<StatusCodeHttpResult>());
        Assert.That(((StatusCodeHttpResult)result).StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
    }

    [Test]
    public async Task GetCatalogAsync_ETagChangesWhenCatalogChanges()
    {
        var before = Http();
        await GameCatalogEndpoint.GetCatalogAsync(before, _context);
        var etagBefore = before.Response.Headers.ETag.ToString();

        var added = Game("4", "New Game", enabled: true, Rule("new.exe", GamePlatform.Win32));
        _context.GameApplications.Add(added);
        await _context.SaveChangesAsync();

        var after = Http();
        await GameCatalogEndpoint.GetCatalogAsync(after, _context);

        Assert.That(after.Response.Headers.ETag.ToString(), Is.Not.EqualTo(etagBefore));
    }

    [Test]
    public async Task GetCatalogAsync_ETagIsPerPlatform()
    {
        // Otherwise a client that switched platforms - or a shared cache - would be told its
        // win32 copy is still valid for a darwin request.
        var win = Http();
        await GameCatalogEndpoint.GetCatalogAsync(win, _context, os: "win32");

        var mac = Http();
        await GameCatalogEndpoint.GetCatalogAsync(mac, _context, os: "darwin");

        Assert.That(win.Response.Headers.ETag.ToString(), Is.Not.EqualTo(mac.Response.Headers.ETag.ToString()));
    }

    [Test]
    public async Task GetCatalogAsync_StaleIfNoneMatch_ReturnsFullBody()
    {
        var result = await GameCatalogEndpoint.GetCatalogAsync(Http(ifNoneMatch: "\"stale\""), _context);

        Assert.That(result, Is.TypeOf<FileContentHttpResult>());
    }

    // ── Compression ─────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetCatalogAsync_AcceptEncodingGzip_ReturnsCompressedBody()
    {
        var http = Http(acceptEncoding: "gzip, deflate, br");

        var result = await GameCatalogEndpoint.GetCatalogAsync(http, _context) as FileContentHttpResult;

        Assert.That(http.Response.Headers.ContentEncoding.ToString(), Is.EqualTo("gzip"));
        Assert.That(Deserialize(result!, gzipped: true).Games, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task GetCatalogAsync_NoAcceptEncoding_ReturnsPlainJson()
    {
        var http = Http();

        var result = await GameCatalogEndpoint.GetCatalogAsync(http, _context) as FileContentHttpResult;

        Assert.That(http.Response.Headers.ContentEncoding.ToString(), Is.Empty);
        Assert.That(Deserialize(result!).Games, Has.Count.EqualTo(2));
    }

    // ── Negative ────────────────────────────────────────────────────────────────────────────

    [TestCase("windows")]
    [TestCase("win64")]
    [TestCase("../etc/passwd")]
    public async Task GetCatalogAsync_UnknownOs_ReturnsBadRequest(string os)
    {
        var result = await GameCatalogEndpoint.GetCatalogAsync(Http(), _context, os: os);

        Assert.That(result, Is.TypeOf<BadRequest<string>>());
    }

    [Test]
    public async Task GetCatalogAsync_EmptyCatalog_ReturnsEmptyListNotAnError()
    {
        await using var empty = new TestSocialContext(Guid.NewGuid().ToString());

        var result = await GameCatalogEndpoint.GetCatalogAsync(Http(), empty) as FileContentHttpResult;

        Assert.That(result, Is.Not.Null);
        Assert.That(Deserialize(result!).Games, Is.Empty);
    }
}
