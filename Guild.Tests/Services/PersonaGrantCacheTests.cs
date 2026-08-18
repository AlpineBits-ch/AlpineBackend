using Guild.Application.Bus.Events.Role;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Events.Role;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Role = Guild.Domain.Aggregates.Role;

namespace Guild.Tests.Services;

/// <summary>
/// A persona grant can name a role, so role membership decides who a guild's shared cast is
/// listed for. The send path re-reads grants, so a stale set is a wrong listing rather than a hole -
/// but a character disappearing for a quarter of an hour is its own bug.
/// </summary>
[TestFixture]
public class PersonaGrantCacheTests
{
    private const string GuildId = "guild-1";
    private const string RoleId = "role-gm";
    private const string MemberId = "member-1";
    private const string UserId = "user-1";
    private const string PersonaId = "pers_narrator";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private PersonaService _personas = null!;
    private GuildPermissionService _permissions = null!;
    private FakeHubContext _hub = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _personas = new PersonaService(_cache, _context);
        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _hub = new FakeHubContext();

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = "owner-1", Name = "Blackwater",
            Features = GuildFeatures.Personas | GuildFeatures.Wiki,
            CreatedAt = Now, UpdatedAt = Now,
        });

        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Name = "gamemaster", Type = RoleType.None,
            Permissions = Permissions.ViewChannel, CreatedAt = Now, UpdatedAt = Now,
        });

        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            SearchValue = UserId.ToUpperInvariant(), CreatedAt = Now, UpdatedAt = Now,
        });

        _context.Set<Persona>().Add(new Persona
        {
            Id = PersonaId, Scope = PersonaScope.Guild, OwnerGuildId = GuildId, Name = "Narrator",
            HasSpoken = true, CreatedAt = Now, UpdatedAt = Now,
        });

        _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
        {
            Id = "pgpf-narrator", PersonaId = PersonaId, GuildId = GuildId,
            ApprovalState = PersonaApprovalState.Approved, CreatedAt = Now, UpdatedAt = Now,
        });

        _context.Set<PersonaGrant>().Add(new PersonaGrant
        {
            Id = "pgnt-1", PersonaId = PersonaId, RoleId = RoleId, CreatedAt = Now, UpdatedAt = Now,
        });

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private async Task<List<string>> ListAsync() =>
        (await _personas.GetUsablePersonasAsync(UserId, GuildId)).Select(p => p.PersonaId).ToList();

    private async Task JoinRoleAsync()
    {
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rome-1", RoleId = RoleId, MemberId = MemberId, CreatedAt = Now, UpdatedAt = Now,
        });

        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task JoiningAGrantedRole_MakesTheCharacterAppearWithoutWaitingOutTheCache()
    {
        Assert.That(await ListAsync(), Is.Empty, "nothing to speak as before the role is held");

        await JoinRoleAsync();

        await RoleUpdatedHandler.Handle(
            new RoleUpdated { RoleId = RoleId, GuildId = GuildId, MemberId = MemberId },
            _context, _permissions, new FakeMessageBus(), _hub, EmptyPresence(), _personas);

        Assert.That(await ListAsync(), Is.EqualTo(new[] { PersonaId }).AsCollection);
    }

    [Test]
    public async Task LeavingAGrantedRole_TakesTheCharacterOutOfTheListing()
    {
        await JoinRoleAsync();
        Assert.That(await ListAsync(), Is.EqualTo(new[] { PersonaId }).AsCollection);

        var membership = _context.RoleMembers.Single(rm => rm.Id == "rome-1");
        _context.RoleMembers.Remove(membership);
        await _context.SaveChangesAsync();

        await RoleUpdatedHandler.Handle(
            new RoleUpdated { RoleId = RoleId, GuildId = GuildId, MemberId = MemberId },
            _context, _permissions, new FakeMessageBus(), _hub, EmptyPresence(), _personas);

        Assert.That(await ListAsync(), Is.Empty);
    }

    [Test]
    public async Task ABulkRoleSet_AlsoInvalidatesTheListing()
    {
        Assert.That(await ListAsync(), Is.Empty);

        await JoinRoleAsync();

        await MemberRolesUpdatedHandler.Handle(
            new MemberRolesUpdated { GuildId = GuildId, MemberId = MemberId, AddedRoleIds = [RoleId] },
            _context, _permissions, new FakeMessageBus(), _hub, EmptyPresence(), _personas);

        Assert.That(await ListAsync(), Is.EqualTo(new[] { PersonaId }).AsCollection);
    }

    /// <summary>Deleting the role cascades the grant, which is the same loss by another route.</summary>
    [Test]
    public async Task DeletingAGrantedRole_TakesTheCharacterOutOfTheListing()
    {
        await JoinRoleAsync();
        Assert.That(await ListAsync(), Is.EqualTo(new[] { PersonaId }).AsCollection);

        _context.Set<PersonaGrant>().RemoveRange(_context.Set<PersonaGrant>());
        _context.Roles.RemoveRange(_context.Roles);
        await _context.SaveChangesAsync();

        await RoleDeletedHandler.Handle(
            new RoleDeleted { RoleId = RoleId, GuildId = GuildId, UserIds = [UserId] },
            _permissions, _hub, EmptyPresence(), new FakeMessageBus(), _personas);

        Assert.That(await ListAsync(), Is.Empty);
    }

    /// <summary>A role's own fields changing says nothing about who holds it.</summary>
    [Test]
    public async Task EditingARolesPermissions_LeavesTheCachedListingStanding()
    {
        await JoinRoleAsync();
        var before = await ListAsync();

        // The grant is torn out behind the cache's back, so a listing that recomputes here would
        // come back empty and the assertion below would fail.
        _context.Set<PersonaGrant>().RemoveRange(_context.Set<PersonaGrant>());
        await _context.SaveChangesAsync();

        await RoleUpdatedHandler.Handle(
            new RoleUpdated { RoleId = RoleId, GuildId = GuildId, MemberId = null },
            _context, _permissions, new FakeMessageBus(), _hub, EmptyPresence(), _personas);

        Assert.That(await ListAsync(), Is.EqualTo(before).AsCollection,
            "membership is what a grant turns on, so a metadata edit does not pay for a recompute");
    }

    private static GuildHydrateService EmptyPresence() =>
        new(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
}
