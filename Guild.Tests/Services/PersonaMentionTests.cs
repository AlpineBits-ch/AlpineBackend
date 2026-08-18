using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Role = Guild.Domain.Aggregates.Role;

namespace Guild.Tests.Services;

/// <summary>
/// Typing <c>@Mayor Cogsgrove</c> notifies whoever plays the Mayor. What it must never do is put
/// that person's id anywhere another reader can see it - see docs/specs/roleplay-guilds.md §2.1.
/// </summary>
[TestFixture]
public class PersonaMentionTests
{
    private const string GuildId = "guild-1";
    private const string ElsewhereGuildId = "guild-elsewhere";
    private const string AuthorId = "user-author";
    private const string OwnerId = "user-owner";
    private const string GmOneId = "user-gm-1";
    private const string GmTwoId = "user-gm-2";
    private const string GmRoleId = "role-gm";

    private TestGuildContext _context = null!;
    private PersonaMentionService _mentions = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        var cache = new FakeDistributedCache();
        _mentions = new PersonaMentionService(_context, new PersonaService(cache, _context));

        foreach (var guildId in new[] { GuildId, ElsewhereGuildId })
        {
            _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
            {
                Id = guildId, OwnerId = AuthorId, Name = guildId,
                Features = GuildFeatures.Personas | GuildFeatures.Wiki,
                CreatedAt = Now, UpdatedAt = Now,
            });
        }

        _context.Roles.Add(new Role
        {
            Id = GmRoleId, GuildId = GuildId, Name = "gamemaster", Type = RoleType.None,
            Permissions = Permissions.ViewChannel, CreatedAt = Now, UpdatedAt = Now,
        });

        AddMember("memb-author", AuthorId);
        AddMember("memb-owner", OwnerId);
        AddMember("memb-gm-1", GmOneId);
        AddMember("memb-gm-2", GmTwoId);

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The token

    [Test]
    public void Parse_FindsTheCharactersAMessageNames()
    {
        var found = PersonaMentionService.Parse("<@pers_mayor> nods at <@pers_guard>.");

        Assert.That(found, Is.EqualTo(new[] { "pers_mayor", "pers_guard" }).AsCollection);
    }

    [Test]
    public void Parse_IgnoresAnOrdinaryUserMention()
    {
        Assert.That(PersonaMentionService.Parse("<@user-1> hello"), Is.Empty,
            "the pers_ prefix is what keeps the two kinds of id apart");
    }

    [Test]
    public void Parse_NamingTheSameCharacterTwice_YieldsOneId()
    {
        Assert.That(PersonaMentionService.Parse("<@pers_mayor> <@pers_mayor>"),
            Is.EqualTo(new[] { "pers_mayor" }).AsCollection);
    }

    [Test]
    public void Parse_HandlesAnEmptyOrTokenlessBody()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PersonaMentionService.Parse((string?)null), Is.Empty);
            Assert.That(PersonaMentionService.Parse(""), Is.Empty);
            Assert.That(PersonaMentionService.Parse((byte[]?)null), Is.Empty);
            Assert.That(PersonaMentionService.Parse("evening, all"), Is.Empty);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Resolution

    [Test]
    public async Task APersonalCharacter_ReachesItsOwner()
    {
        await SeedUserPersonaAsync("pers_mayor", OwnerId, GuildId);

        var targets = await _mentions.ResolveAsync(GuildId, AuthorId, ["pers_mayor"]);

        Assert.That(targets.Select(t => t.UserId), Is.EqualTo(new[] { OwnerId }).AsCollection);
    }

    [Test]
    public async Task AGuildCharacter_ReachesEveryCurrentGrantHolder()
    {
        await SeedGuildPersonaAsync("pers_narrator");
        await GrantRoleAsync("pers_narrator", GmRoleId);
        await JoinRoleAsync("memb-gm-1");
        await JoinRoleAsync("memb-gm-2");

        var targets = await _mentions.ResolveAsync(GuildId, AuthorId, ["pers_narrator"]);

        Assert.That(targets.Select(t => t.UserId), Is.EquivalentTo(new[] { GmOneId, GmTwoId }),
            "a shared character has no owner, so everyone answerable for it is told");
    }

    [Test]
    public async Task AGuildCharacterNobodyHoldsAGrantOn_ReachesNobody()
    {
        await SeedGuildPersonaAsync("pers_narrator");

        Assert.That(await _mentions.ResolveAsync(GuildId, AuthorId, ["pers_narrator"]), Is.Empty);
    }

    [Test]
    public async Task NamingYourOwnCharacter_ReachesNobody()
    {
        await SeedUserPersonaAsync("pers_mayor", AuthorId, GuildId);

        Assert.That(await _mentions.ResolveAsync(GuildId, AuthorId, ["pers_mayor"]), Is.Empty);
    }

    [Test]
    public async Task ACharacterNotAdoptedIntoThisGuild_ReachesNobody()
    {
        await SeedUserPersonaAsync("pers_mayor", OwnerId, ElsewhereGuildId);

        Assert.That(await _mentions.ResolveAsync(GuildId, AuthorId, ["pers_mayor"]), Is.Empty,
            "adoption is what puts a character in a guild");
    }

    [Test]
    public async Task ARetiredCharacter_ReachesNobody()
    {
        await SeedUserPersonaAsync("pers_mayor", OwnerId, GuildId, retired: true);

        Assert.That(await _mentions.ResolveAsync(GuildId, AuthorId, ["pers_mayor"]), Is.Empty,
            "retiring is how somebody puts a character away, and a ping is what they put away");
    }

    [Test]
    public async Task AnUnknownId_ReachesNobodyAndDoesNotThrow()
    {
        Assert.That(await _mentions.ResolveAsync(GuildId, AuthorId, ["pers_does-not-exist"]), Is.Empty);
    }

    [Test]
    public async Task TwoCharactersOwnedByTheSamePerson_AreReportedOncePerCharacter()
    {
        await SeedUserPersonaAsync("pers_mayor", OwnerId, GuildId);
        await SeedUserPersonaAsync("pers_miller", OwnerId, GuildId);

        var targets = await _mentions.ResolveAsync(GuildId, AuthorId, ["pers_mayor", "pers_miller"]);

        Assert.That(targets.Select(t => t.PersonaId), Is.EquivalentTo(new[] { "pers_mayor", "pers_miller" }));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Seeding
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private void AddMember(string memberId, string userId) =>
        _context.GuildMembers.Add(new GuildMember
        {
            Id = memberId, GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
            SearchValue = userId.ToUpperInvariant(), CreatedAt = Now, UpdatedAt = Now,
        });

    private async Task SeedUserPersonaAsync(string personaId, string ownerId, string guildId, bool retired = false)
    {
        _context.Set<Persona>().Add(new Persona
        {
            Id = personaId, Scope = PersonaScope.User, OwnerUserId = ownerId, Name = personaId,
            IsRetired = retired, HasSpoken = true, CreatedAt = Now, UpdatedAt = Now,
        });

        _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
        {
            Id = $"pgpf-{personaId}-{guildId}", PersonaId = personaId, GuildId = guildId,
            ApprovalState = PersonaApprovalState.Approved, CreatedAt = Now, UpdatedAt = Now,
        });

        await _context.SaveChangesAsync();
    }

    private async Task SeedGuildPersonaAsync(string personaId)
    {
        _context.Set<Persona>().Add(new Persona
        {
            Id = personaId, Scope = PersonaScope.Guild, OwnerGuildId = GuildId, Name = personaId,
            HasSpoken = true, CreatedAt = Now, UpdatedAt = Now,
        });

        _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
        {
            Id = $"pgpf-{personaId}", PersonaId = personaId, GuildId = GuildId,
            ApprovalState = PersonaApprovalState.Approved, CreatedAt = Now, UpdatedAt = Now,
        });

        await _context.SaveChangesAsync();
    }

    private async Task GrantRoleAsync(string personaId, string roleId)
    {
        _context.Set<PersonaGrant>().Add(new PersonaGrant
        {
            Id = $"pgnt-{personaId}-{roleId}", PersonaId = personaId, RoleId = roleId,
            CreatedAt = Now, UpdatedAt = Now,
        });

        await _context.SaveChangesAsync();
    }

    private async Task JoinRoleAsync(string memberId)
    {
        _context.RoleMembers.Add(new RoleMember
        {
            Id = $"rome-{memberId}", RoleId = GmRoleId, MemberId = memberId,
            CreatedAt = Now, UpdatedAt = Now,
        });

        await _context.SaveChangesAsync();
    }
}
