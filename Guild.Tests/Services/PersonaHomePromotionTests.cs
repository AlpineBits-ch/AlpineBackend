using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Endpoints.Guild;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Services;

/// <summary>
/// Losing the guild that holds the reference copy of a character page must not orphan the
/// character. HomeProfileId carries no foreign key, so nothing cascades it and the promotion is an
/// explicit sweep - see docs/specs/roleplay-guilds.md §3.3 and §4.3.
/// </summary>
[TestFixture]
public class PersonaHomePromotionTests
{
    private const string HomeGuildId = "guild-home";
    private const string OtherGuildId = "guild-other";
    private const string ThirdGuildId = "guild-third";
    private const string OwnerId = "user-owner";
    private const string PersonaId = "pers_mayor";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private PersonaService _personas = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _personas = new PersonaService(_cache, _context);

        foreach (var guildId in new[] { HomeGuildId, OtherGuildId, ThirdGuildId })
        {
            _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
            {
                Id = guildId, OwnerId = OwnerId, Name = guildId,
                Features = GuildFeatures.Personas | GuildFeatures.Wiki,
                CreatedAt = Now, UpdatedAt = Now,
            });
        }

        _context.Set<Persona>().Add(new Persona
        {
            Id = PersonaId, Scope = PersonaScope.User, OwnerUserId = OwnerId,
            Name = "Mayor Cogsgrove", HasSpoken = true, CreatedAt = Now, UpdatedAt = Now,
        });

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Guild deletion

    [Test]
    public async Task DeletingTheReferenceGuild_PromotesTheOldestRemainingCopy()
    {
        await AdoptAsync(HomeGuildId, "pgpf-home", pageId: "wiki-home", createdAt: Now.AddDays(-3));
        await AdoptAsync(OtherGuildId, "pgpf-other", pageId: "wiki-other", createdAt: Now.AddDays(-2),
            upstreamRevision: 7);
        await AdoptAsync(ThirdGuildId, "pgpf-third", pageId: "wiki-third", createdAt: Now.AddDays(-1),
            upstreamRevision: 4);
        await SetHomeAsync("pgpf-home");

        await _personas.RepointHomesForDeletedGuildAsync(HomeGuildId);
        await _context.SaveChangesAsync();

        Assert.That(await HomeProfileIdAsync(), Is.EqualTo("pgpf-other"),
            "the oldest surviving copy that has a page becomes the reference");
    }

    [Test]
    public async Task Promotion_ClearsTheUpstreamRevisionOnEverySurvivingCopy()
    {
        await AdoptAsync(HomeGuildId, "pgpf-home", pageId: "wiki-home", createdAt: Now.AddDays(-3));
        await AdoptAsync(OtherGuildId, "pgpf-other", pageId: "wiki-other", createdAt: Now.AddDays(-2),
            upstreamRevision: 7);
        await AdoptAsync(ThirdGuildId, "pgpf-third", pageId: "wiki-third", createdAt: Now.AddDays(-1),
            upstreamRevision: 4);
        await SetHomeAsync("pgpf-home");

        await _personas.RepointHomesForDeletedGuildAsync(HomeGuildId);
        await _context.SaveChangesAsync();

        var survivors = await _context.Set<PersonaGuildProfile>()
            .AsNoTracking()
            .Where(p => p.GuildId != HomeGuildId)
            .ToListAsync();

        Assert.That(survivors.Select(p => p.UpstreamRevisionNumber), Is.All.Null,
            "those numbers came from a history the survivors never shared, so they read as diverged");
    }

    [Test]
    public async Task Promotion_MakesTheSurvivorsReadAsDivergedRatherThanBehind()
    {
        await AdoptAsync(HomeGuildId, "pgpf-home", pageId: "wiki-home", createdAt: Now.AddDays(-2));
        await AdoptAsync(OtherGuildId, "pgpf-other", pageId: "wiki-other", createdAt: Now.AddDays(-1),
            upstreamRevision: 7);
        await AdoptAsync(ThirdGuildId, "pgpf-third", pageId: "wiki-third", createdAt: Now,
            upstreamRevision: 7);
        await SetHomeAsync("pgpf-home");

        await _personas.RepointHomesForDeletedGuildAsync(HomeGuildId);
        await _context.SaveChangesAsync();

        var persona = await _context.Set<Persona>().AsNoTracking().SingleAsync(p => p.Id == PersonaId);
        var third = await _context.Set<PersonaGuildProfile>().AsNoTracking().SingleAsync(p => p.Id == "pgpf-third");

        var state = await new PersonaPageService(_context).ResolveUpstreamStateAsync(persona, third);

        Assert.That(state, Is.EqualTo(PersonaUpstreamState.Diverged));
    }

    [Test]
    public async Task DeletingTheOnlyGuild_LeavesThePersonaWithANullPointer()
    {
        await AdoptAsync(HomeGuildId, "pgpf-home", pageId: "wiki-home", createdAt: Now);
        await SetHomeAsync("pgpf-home");

        await _personas.RepointHomesForDeletedGuildAsync(HomeGuildId);
        await _context.SaveChangesAsync();

        var survives = await _context.Set<Persona>().AsNoTracking().AnyAsync(p => p.Id == PersonaId);
        var home = await HomeProfileIdAsync();

        Assert.Multiple(() =>
        {
            Assert.That(survives, Is.True, "the character outlives every copy of its page");
            Assert.That(home, Is.Null);
        });
    }

    /// <summary>A copy nobody ever wrote a page for is not a reference copy.</summary>
    [Test]
    public async Task DeletingTheReferenceGuild_SkipsASurvivingCopyWithNoPage()
    {
        await AdoptAsync(HomeGuildId, "pgpf-home", pageId: "wiki-home", createdAt: Now.AddDays(-1));
        await AdoptAsync(OtherGuildId, "pgpf-other", pageId: null, createdAt: Now);
        await SetHomeAsync("pgpf-home");

        await _personas.RepointHomesForDeletedGuildAsync(HomeGuildId);
        await _context.SaveChangesAsync();

        Assert.That(await HomeProfileIdAsync(), Is.Null);
    }

    [Test]
    public async Task DeletingAGuildThatIsNotTheReference_LeavesThePointerAlone()
    {
        await AdoptAsync(HomeGuildId, "pgpf-home", pageId: "wiki-home", createdAt: Now.AddDays(-1));
        await AdoptAsync(OtherGuildId, "pgpf-other", pageId: "wiki-other", createdAt: Now, upstreamRevision: 7);
        await SetHomeAsync("pgpf-home");

        await _personas.RepointHomesForDeletedGuildAsync(OtherGuildId);
        await _context.SaveChangesAsync();

        var other = await _context.Set<PersonaGuildProfile>().AsNoTracking().SingleAsync(p => p.Id == "pgpf-other");
        var home = await HomeProfileIdAsync();

        Assert.Multiple(() =>
        {
            Assert.That(home, Is.EqualTo("pgpf-home"));
            Assert.That(other.UpstreamRevisionNumber, Is.EqualTo(7),
                "nothing was promoted, so nothing diverged");
        });
    }

    [Test]
    public async Task DeletingAGuildWithNoPersonasInIt_DoesNothing()
    {
        await AdoptAsync(HomeGuildId, "pgpf-home", pageId: "wiki-home", createdAt: Now);
        await SetHomeAsync("pgpf-home");

        await _personas.RepointHomesForDeletedGuildAsync(ThirdGuildId);
        await _context.SaveChangesAsync();

        Assert.That(await HomeProfileIdAsync(), Is.EqualTo("pgpf-home"));
    }

    /// <summary>The endpoint is the call site that was missing the sweep entirely.</summary>
    [Test]
    public async Task DeleteGuildEndpoint_RunsTheSweepBeforeTheGuildGoes()
    {
        await AdoptAsync(HomeGuildId, "pgpf-home", pageId: "wiki-home", createdAt: Now.AddDays(-1));
        await AdoptAsync(OtherGuildId, "pgpf-other", pageId: "wiki-other", createdAt: Now, upstreamRevision: 7);
        await SetHomeAsync("pgpf-home");

        var result = await new GuildEndpoint().DeleteGuild(
            HomeGuildId, _context, TestPrincipal.Create(OwnerId), new FakeHubContext(), _personas);

        var home = await HomeProfileIdAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NoContent>());
            Assert.That(home, Is.EqualTo("pgpf-other"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Un-adoption, which shares the same sweep

    [Test]
    public async Task UnAdoptingTheReferenceCopy_PromotesAnotherOne()
    {
        await AdoptAsync(HomeGuildId, "pgpf-home", pageId: "wiki-home", createdAt: Now.AddDays(-1));
        await AdoptAsync(OtherGuildId, "pgpf-other", pageId: "wiki-other", createdAt: Now, upstreamRevision: 7);
        await SetHomeAsync("pgpf-home");

        var persona = await _context.Set<Persona>().SingleAsync(p => p.Id == PersonaId);
        await _personas.RepointHomeAsync(persona, "pgpf-home");
        await _context.SaveChangesAsync();

        var other = await _context.Set<PersonaGuildProfile>().AsNoTracking().SingleAsync(p => p.Id == "pgpf-other");
        var home = await HomeProfileIdAsync();

        Assert.Multiple(() =>
        {
            Assert.That(home, Is.EqualTo("pgpf-other"));
            Assert.That(other.UpstreamRevisionNumber, Is.Null);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Seeding
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private async Task AdoptAsync(
        string guildId, string profileId, string? pageId, DateTimeOffset createdAt, int? upstreamRevision = null)
    {
        _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
        {
            Id = profileId, PersonaId = PersonaId, GuildId = guildId, WikiPageId = pageId,
            UpstreamRevisionNumber = upstreamRevision, ApprovalState = PersonaApprovalState.Approved,
            CreatedAt = createdAt, UpdatedAt = createdAt,
        });

        await _context.SaveChangesAsync();
    }

    private async Task SetHomeAsync(string profileId)
    {
        var persona = await _context.Set<Persona>().SingleAsync(p => p.Id == PersonaId);
        persona.HomeProfileId = profileId;
        await _context.SaveChangesAsync();
    }

    private async Task<string?> HomeProfileIdAsync() =>
        (await _context.Set<Persona>().AsNoTracking().SingleAsync(p => p.Id == PersonaId)).HomeProfileId;
}
