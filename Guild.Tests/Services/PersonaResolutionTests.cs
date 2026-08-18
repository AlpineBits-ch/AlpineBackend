using Guild.Application.Services;
using Guild.Contracts.Bus.Response;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;

namespace Guild.Tests.Services;

/// <summary>
/// The send path: which persona a message resolves to, and the three ways it must refuse. The
/// invariant underneath all of it is that AuthorId is never part of the answer.
/// </summary>
[TestFixture]
public class PersonaResolutionTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "chan-1";
    private const string UserId = "user-1";
    private const string MemberId = "member-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private PersonaService _personas = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _personas = new PersonaService(_cache, _context);

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = "owner-1", Name = "Blackwater",
            Features = GuildFeatures.Personas | GuildFeatures.Wiki,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "the-inn", Type = ChannelType.Text,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Resolution order

    [Test]
    public async Task Resolve_ExplicitPersonaId_BeatsAPrefixMatchOnTheSameMessage()
    {
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", prefix: "M:");
        await SeedOwnPersonaAsync("guard", "Town Guard");

        var result = await ResolveAsync("M: evening", personaId: "guard");

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(PersonaResolutionOutcome.Resolved));
            Assert.That(result.PersonaId, Is.EqualTo("guard"));
            Assert.That(result.Content, Is.EqualTo("M: evening"), "an explicit id strips nothing");
        });
    }

    [Test]
    public async Task Resolve_PrefixAndSuffix_AreStrippedFromTheStoredContent()
    {
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", prefix: "M:", suffix: "]]");

        var result = await ResolveAsync("M: evening, all]]");

        Assert.Multiple(() =>
        {
            Assert.That(result.PersonaId, Is.EqualTo("mayor"));
            Assert.That(result.Content, Is.EqualTo("evening, all"));
        });
    }

    [Test]
    public async Task Resolve_NoPrefixMatch_FallsThroughToTheChannelAutoproxy()
    {
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", prefix: "M:");
        await SeedAutoproxyAsync(AutoproxyMode.Pinned, "mayor");

        var result = await ResolveAsync("evening, all");

        Assert.Multiple(() =>
        {
            Assert.That(result.PersonaId, Is.EqualTo("mayor"));
            Assert.That(result.Content, Is.EqualTo("evening, all"), "autoproxy strips nothing");
        });
    }

    [Test]
    public async Task Resolve_NothingMatches_SendsAsTheUser()
    {
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", prefix: "M:");

        var result = await ResolveAsync("evening, all");

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(PersonaResolutionOutcome.None));
            Assert.That(result.PersonaId, Is.Null);
        });
    }

    [Test]
    public async Task Resolve_LeadingBackslash_SuppressesProxyingAndIsStripped()
    {
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", prefix: "M:");
        await SeedAutoproxyAsync(AutoproxyMode.Pinned, "mayor");

        var result = await ResolveAsync("\\M: this one is me talking");

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(PersonaResolutionOutcome.None));
            Assert.That(result.Content, Is.EqualTo("M: this one is me talking"));
        });
    }

    /// <summary>
    /// The switcher selection arrives as an explicit id on every message, so ranked below it the
    /// escape both leaked into the body and failed to drop the sender out of character.
    /// </summary>
    [Test]
    public async Task Resolve_LeadingBackslash_BeatsAnExplicitPersonaId()
    {
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", prefix: "M:");

        var result = await ResolveAsync("\\hello", personaId: "mayor");

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(PersonaResolutionOutcome.None));
            Assert.That(result.PersonaId, Is.Null);
            Assert.That(result.Content, Is.EqualTo("hello"), "and the escape never reaches the body");
        });
    }

    /// <summary>Sticky follows whoever spoke in character, and an escaped message did not.</summary>
    [Test]
    public async Task Resolve_LeadingBackslash_DoesNotLatchSticky()
    {
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", prefix: "M:");
        await SeedAutoproxyAsync(AutoproxyMode.Sticky, personaId: null);

        await ResolveAsync("\\hello", personaId: "mayor");
        await _context.SaveChangesAsync();

        var next = await ResolveAsync("and another thing");

        Assert.That(next.Outcome, Is.EqualTo(PersonaResolutionOutcome.None));
    }

    [Test]
    public async Task Resolve_LongestPrefixWins_SoAnOverlappingPairIsDeterministic()
    {
        await SeedOwnPersonaAsync("short", "M", prefix: "M");
        await SeedOwnPersonaAsync("long", "Mayor", prefix: "M:");

        var result = await ResolveAsync("M: evening");

        Assert.That(result.PersonaId, Is.EqualTo("long"));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Refusals

    [Test]
    public async Task Resolve_APersonaTheCallerCannotReach_IsForbidden()
    {
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", prefix: "M:", ownerUserId: "somebody-else");

        var result = await ResolveAsync("hello", personaId: "mayor");

        Assert.That(result.Outcome, Is.EqualTo(PersonaResolutionOutcome.Forbidden));
    }

    [Test]
    public async Task Resolve_RevokedGrant_StopsSpeechEvenWithTheSetStillCached()
    {
        await SeedGuildPersonaAsync("narrator", "Narrator", prefix: "N:");
        var grant = await SeedGrantAsync("narrator", userId: UserId);

        var before = await ResolveAsync("N: the road forks");
        Assert.That(before.PersonaId, Is.EqualTo("narrator"), "the grant holds to begin with");

        // Removed without touching the cache, which is exactly the window the send path has to
        // close by re-reading the grant rather than trusting the cached set.
        _context.Set<PersonaGrant>().Remove(grant);
        await _context.SaveChangesAsync();

        var after = await ResolveAsync("the road forks", personaId: "narrator");

        Assert.That(after.Outcome, Is.EqualTo(PersonaResolutionOutcome.Forbidden));
    }

    [Test]
    public async Task Resolve_AutoproxyOntoALostGrant_SendsAsTheUserRatherThanFailing()
    {
        await SeedGuildPersonaAsync("narrator", "Narrator");
        var grant = await SeedGrantAsync("narrator", userId: UserId);
        await SeedAutoproxyAsync(AutoproxyMode.Pinned, "narrator");

        await ResolveAsync("warming the cache");

        _context.Set<PersonaGrant>().Remove(grant);
        await _context.SaveChangesAsync();

        var result = await ResolveAsync("the road forks");

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(PersonaResolutionOutcome.None),
                "the user did not ask for the persona on this message, so the send still goes through");
            Assert.That(result.Content, Is.EqualTo("the road forks"));
        });
    }

    [Test]
    public async Task Resolve_RetiredPersona_IsForbidden()
    {
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", prefix: "M:", retired: true);

        var result = await ResolveAsync("M: evening");

        Assert.That(result.Outcome, Is.EqualTo(PersonaResolutionOutcome.Forbidden));
    }

    [Test]
    public async Task Resolve_GuildRequiringApproval_BlocksAnUnapprovedPersona()
    {
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", prefix: "M:",
            approval: PersonaApprovalState.Submitted);

        var guild = await _context.Guilds.FindAsync(GuildId);
        guild!.RequirePersonaApproval = true;
        await _context.SaveChangesAsync();

        var result = await ResolveAsync("M: evening");

        Assert.That(result.Outcome, Is.EqualTo(PersonaResolutionOutcome.Forbidden));
    }

    [Test]
    public async Task Resolve_GuildRequiringApproval_LetsAnApprovedPersonaSpeak()
    {
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", prefix: "M:",
            approval: PersonaApprovalState.Approved);

        var guild = await _context.Guilds.FindAsync(GuildId);
        guild!.RequirePersonaApproval = true;
        await _context.SaveChangesAsync();

        var result = await ResolveAsync("M: evening");

        Assert.That(result.PersonaId, Is.EqualTo("mayor"));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The invariant

    [Test]
    public void Resolution_CarriesNoAuthorId_InEitherDirection()
    {
        // The whole feature rests on AuthorId staying the real account: if either shape ever grows
        // one, a caller can be told to overwrite it.
        Assert.Multiple(() =>
        {
            Assert.That(typeof(PersonaResolution).GetProperty("AuthorId"), Is.Null);
            Assert.That(typeof(ResolvePersonaForSendResponse).GetProperty("AuthorId"), Is.Null);
        });
    }

    [Test]
    public async Task Resolve_SetsOnlyTheDisplayOverrides()
    {
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", prefix: "M:", avatarUrl: "/media/mayor.png");

        var result = await ResolveAsync("M: evening");

        Assert.Multiple(() =>
        {
            Assert.That(result.DisplayName, Is.EqualTo("Mayor Cogsgrove"));
            Assert.That(result.AvatarUrl, Is.EqualTo("/media/mayor.png"));
            Assert.That(result.PersonaId, Is.EqualTo("mayor"));
        });
    }

    [Test]
    public async Task Resolve_StampsHasSpoken_SoALaterDeleteRetiresInstead()
    {
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", prefix: "M:", hasSpoken: false);

        await ResolveAsync("M: evening");
        await _context.SaveChangesAsync();

        var persona = await _context.Set<Persona>().FindAsync("mayor");

        Assert.That(persona!.HasSpoken, Is.True);
    }

    [Test]
    public async Task Resolve_Sticky_RemembersWhoeverSpokeLast()
    {
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", prefix: "M:");
        await SeedAutoproxyAsync(AutoproxyMode.Sticky, personaId: null);

        await ResolveAsync("M: evening");
        await _context.SaveChangesAsync();

        var plain = await ResolveAsync("and another thing");

        Assert.That(plain.PersonaId, Is.EqualTo("mayor"));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Prefix uniqueness

    [Test]
    public void AffixesCollide_TreatsAnOverlappingPrefixAsACollision()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PersonaService.AffixesCollide("M:", null, "M:", null), Is.True, "identical");
            Assert.That(PersonaService.AffixesCollide("M", null, "M:", null), Is.True,
                "\"M\" fires on every message \"M:\" fires on");
            Assert.That(PersonaService.AffixesCollide("M:", null, "G:", null), Is.False);
            Assert.That(PersonaService.AffixesCollide("M:", "!", "M:", "?"), Is.False,
                "different suffixes disambiguate the same prefix");
            Assert.That(PersonaService.AffixesCollide("M:", "!", "M:", null), Is.True,
                "an unsuffixed prefix swallows the suffixed one");
            Assert.That(PersonaService.AffixesCollide(null, null, "M:", null), Is.False,
                "a persona with no proxy affixes cannot collide with anything");
        });
    }

    [Test]
    public async Task FindProxyCollision_NamesTheOtherPersonaTheCallerCouldHaveMeant()
    {
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", prefix: "M:");
        var newcomer = await SeedOwnPersonaAsync("miller", "The Miller", prefix: null);

        var collision = await _personas.FindProxyCollisionAsync(GuildId, newcomer, "M:", null);

        Assert.Multiple(() =>
        {
            Assert.That(collision, Is.Not.Null);
            Assert.That(collision!.PersonaId, Is.EqualTo("mayor"));
            Assert.That(collision.Name, Is.EqualTo("Mayor Cogsgrove"));
        });
    }

    [Test]
    public async Task FindProxyCollision_SpansTheGrantedGuildPersonasToo()
    {
        await SeedGuildPersonaAsync("narrator", "Narrator", prefix: "N:");
        await SeedGrantAsync("narrator", userId: UserId);
        var mine = await SeedOwnPersonaAsync("nell", "Nell", prefix: null);

        var collision = await _personas.FindProxyCollisionAsync(GuildId, mine, "N:", null);

        Assert.That(collision?.PersonaId, Is.EqualTo("narrator"),
            "the uniqueness rule spans both tables, which is why it cannot be an index");
    }

    [Test]
    public async Task FindProxyCollision_IgnoresAnotherUsersPersonalPrefix()
    {
        await SeedOwnPersonaAsync("theirs", "Their Character", prefix: "M:", ownerUserId: "somebody-else");
        var mine = await SeedOwnPersonaAsync("mine", "My Character", prefix: null);

        var collision = await _personas.FindProxyCollisionAsync(GuildId, mine, "M:", null);

        Assert.That(collision, Is.Null, "two members may each type M: for their own character");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Seeding
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private Task<PersonaResolution> ResolveAsync(string content, string? personaId = null) =>
        _personas.ResolveForSendAsync(new PersonaSendContext
        {
            UserId = UserId,
            GuildId = GuildId,
            ChannelId = ChannelId,
            PersonaId = personaId,
            Content = content,
        });

    /// <summary>
    /// Seeds a persona that has already spoken, so the first resolve does not stamp HasSpoken and
    /// invalidate the very cache a test is about to assert on.
    /// </summary>
    private async Task<Persona> SeedOwnPersonaAsync(
        string id, string name, string? prefix = null, string? suffix = null, bool retired = false,
        string? ownerUserId = null, string? avatarUrl = null, bool hasSpoken = true,
        PersonaApprovalState approval = PersonaApprovalState.Approved)
    {
        var persona = new Persona
        {
            Id = id, Scope = PersonaScope.User, OwnerUserId = ownerUserId ?? UserId,
            Name = name, AvatarUrl = avatarUrl, IsRetired = retired, HasSpoken = hasSpoken,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };

        _context.Set<Persona>().Add(persona);
        _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
        {
            Id = $"profile-{id}", PersonaId = id, GuildId = GuildId,
            ProxyPrefix = prefix, ProxySuffix = suffix, ApprovalState = approval,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
        return persona;
    }

    private async Task<Persona> SeedGuildPersonaAsync(string id, string name, string? prefix = null)
    {
        var persona = new Persona
        {
            Id = id, Scope = PersonaScope.Guild, OwnerGuildId = GuildId, Name = name, HasSpoken = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };

        _context.Set<Persona>().Add(persona);
        _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
        {
            Id = $"profile-{id}", PersonaId = id, GuildId = GuildId, ProxyPrefix = prefix,
            ApprovalState = PersonaApprovalState.Approved,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
        return persona;
    }

    private async Task<PersonaGrant> SeedGrantAsync(string personaId, string? userId = null, string? roleId = null)
    {
        var grant = new PersonaGrant
        {
            Id = $"grant-{personaId}-{userId ?? roleId}", PersonaId = personaId, UserId = userId, RoleId = roleId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };

        _context.Set<PersonaGrant>().Add(grant);
        await _context.SaveChangesAsync();
        return grant;
    }

    private async Task SeedAutoproxyAsync(AutoproxyMode mode, string? personaId)
    {
        _context.Set<PersonaAutoproxyState>().Add(new PersonaAutoproxyState
        {
            Id = $"autoproxy-{ChannelId}-{UserId}", GuildId = GuildId, ChannelId = ChannelId,
            UserId = UserId, Mode = mode, PersonaId = personaId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
    }
}
