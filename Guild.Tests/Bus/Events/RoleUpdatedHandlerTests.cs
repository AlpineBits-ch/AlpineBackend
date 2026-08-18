using System.Text.Json;
using Guild.Application.Bus.Events.Role;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Events.Role;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Role = Guild.Domain.Aggregates.Role;

namespace Guild.Tests.Bus.Events;

[TestFixture]
public class RoleUpdatedHandlerTests
{
    private const string GuildId = "guild-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1"; // GuildMember.Id (prefixed entity ID)
    private const string UserId = "user-1";     // GuildMember.UserId (auth identity - the cache key)

    private string _dbName = null!;
    private FakeDistributedCache _cache = null!;
    private TestGuildContext _context = null!;
    private GuildPermissionService _service = null!;
    private FakeHubContext _hub = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _cache = new FakeDistributedCache();
        _context = new TestGuildContext(_dbName);
        _service = new GuildPermissionService(
            _cache, _context, NullLogger<GuildPermissionService>.Instance);
        _hub = new FakeHubContext();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private FakeHubClients Clients => (FakeHubClients)_hub.Clients;

    private PersonaService Personas() => new(_cache, _context);

    /// <summary>Nobody online anywhere. Every handler still has to run its cache work.</summary>
    private static GuildHydrateService EmptyPresence() =>
        new(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);

    /// <summary>Presence stubbed for one guild's key only, so a broadcast addressed to any other
    /// guild resolves to nobody. That is the audience boundary under test: a role event reaches
    /// exactly the members of its own guild, because <c>guild:presence:{guildId}</c> is the only
    /// place the recipient list ever comes from.</summary>
    private static GuildHydrateService PresenceIn(string guildId, params (string MemberId, string UserId)[] online)
    {
        var database = Substitute.For<IDatabase>();

        database.SortedSetRangeByScoreAsync(
                Arg.Any<RedisKey>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<Exclude>(),
                Arg.Any<Order>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(Array.Empty<RedisValue>()));

        database.SortedSetRangeByScoreAsync(
                (RedisKey)$"guild:presence:{guildId}", Arg.Any<double>(), Arg.Any<double>(), Arg.Any<Exclude>(),
                Arg.Any<Order>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(online.Select(o => (RedisValue)o.MemberId).ToArray()));

        var batch = Substitute.For<IBatch>();
        foreach (var (memberId, userId) in online)
        {
            var state = new MemberPresenceState { MemberId = memberId, UserId = userId, Status = "online" };
            batch.HashGetAllAsync($"presence:user:{memberId}", Arg.Any<CommandFlags>())
                .Returns(Task.FromResult(new[] { new HashEntry("state", JsonSerializer.Serialize(state)) }));
        }

        database.CreateBatch(Arg.Any<object?>()).Returns(batch);

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        return new GuildHydrateService(multiplexer, NullLogger<GuildHydrateService>.Instance);
    }

    private static Role MakeRole(string id = RoleId, Permissions permissions = Permissions.SendMessages) => new()
    {
        Id = id, GuildId = GuildId, Type = RoleType.None, Name = "test-role",
        Permissions = permissions,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static GuildMember MakeGuildMember(string id = MemberId, string userId = UserId) => new()
    {
        Id = id, GuildId = GuildId, UserId = userId,
        JoinedAt = DateTime.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        SearchValue = $"{userId}#{GuildId}",
    };

    private static RoleMember MakeRoleMember(string id, string roleId, string memberId) => new()
    {
        Id = id, RoleId = roleId, MemberId = memberId,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Test]
    public async Task Handle_RemoveMember_InvalidatesRemovedMemberCacheImmediately()
    {
        // Post-removal DB state: role exists but member is no longer in role.Members.
        _context.Roles.Add(MakeRole());
        _context.GuildMembers.Add(MakeGuildMember());
        await _context.SaveChangesAsync();

        var cacheKey = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        _cache.SetEntry(cacheKey, "stale-data");

        await RoleUpdatedHandler.Handle(
            new RoleUpdated { RoleId = RoleId, GuildId = GuildId, MemberId = MemberId },
            _context, _service, new FakeMessageBus(), _hub, EmptyPresence(), Personas());

        Assert.That(_cache.HasEntry(cacheKey), Is.False,
            "Removed member's cache must be cleared immediately so the permission loss takes effect");
    }

    [Test]
    public async Task Handle_AddMember_InvalidatesAddedMemberCache()
    {
        // Post-add DB state: the new RoleMember is already persisted.
        _context.Roles.Add(MakeRole());
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();

        var cacheKey = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        _cache.SetEntry(cacheKey, "stale-data");

        await RoleUpdatedHandler.Handle(
            new RoleUpdated { RoleId = RoleId, GuildId = GuildId, MemberId = MemberId },
            _context, _service, new FakeMessageBus(), _hub, EmptyPresence(), Personas());

        Assert.That(_cache.HasEntry(cacheKey), Is.False,
            "Added member's cache must be cleared so new role permissions take effect immediately");
    }

    [Test]
    public async Task Handle_UpdateRolePermissions_InvalidatesAllMembersCache()
    {
        const string MemberId2 = "member-2";
        const string UserId2 = "user-2";

        _context.Roles.Add(MakeRole());
        _context.GuildMembers.AddRange(MakeGuildMember(), MakeGuildMember(MemberId2, UserId2));
        _context.RoleMembers.AddRange(
            MakeRoleMember("rm-1", RoleId, MemberId),
            MakeRoleMember("rm-2", RoleId, MemberId2));
        await _context.SaveChangesAsync();

        var key1 = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        var key2 = GuildPermissionsForUser.GetCacheKey(GuildId, UserId2);
        _cache.SetEntry(key1, "stale-1");
        _cache.SetEntry(key2, "stale-2");

        // Role permissions updated - no specific member added or removed.
        await RoleUpdatedHandler.Handle(
            new RoleUpdated { RoleId = RoleId, GuildId = GuildId, MemberId = null },
            _context, _service, new FakeMessageBus(), _hub, EmptyPresence(), Personas());

        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry(key1), Is.False, "First member cache invalidated");
            Assert.That(_cache.HasEntry(key2), Is.False, "Second member cache invalidated");
        });
    }

    // ══════════════════════════════════════════════════════════════════════ RoleCreatedHandler -
    // R15 ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task RoleCreated_BroadcastsToTheGuildAndTellsTheBots()
    {
        _context.Roles.Add(MakeRole());
        await _context.SaveChangesAsync();
        var bus = new FakeMessageBus();

        await RoleCreatedHandler.Handle(
            new RoleCreated { RoleId = RoleId, GuildId = GuildId },
            _context, _hub, PresenceIn(GuildId, (MemberId, UserId)), bus);

        Assert.Multiple(() =>
        {
            Assert.That(Clients.RecipientsOf("guild.RoleCreated"), Is.EquivalentTo(new[] { UserId }));
            Assert.That(bus.Published.OfType<RoleCreatedForBots>().Count(), Is.EqualTo(1));
        });

        var forBots = bus.Published.OfType<RoleCreatedForBots>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(forBots.GuildId, Is.EqualTo(GuildId));
            Assert.That(forBots.Role.Id, Is.EqualTo(RoleId));
            Assert.That(forBots.Role.Permissions, Is.EqualTo((ulong)Permissions.SendMessages));
        });
    }

    [Test]
    public async Task RoleCreated_NobodyOnlineInThisGuild_SendsNoRealtimeButStillTellsTheBots()
    {
        _context.Roles.Add(MakeRole());
        await _context.SaveChangesAsync();
        var bus = new FakeMessageBus();

        // Presence exists, but for a different guild - which is exactly the shape of "a user who
        // cannot see this guild".
        await RoleCreatedHandler.Handle(
            new RoleCreated { RoleId = RoleId, GuildId = GuildId },
            _context, _hub, PresenceIn("guild-elsewhere", ("member-x", "user-outsider")), bus);

        Assert.Multiple(() =>
        {
            Assert.That(Clients.SentMessages, Is.Empty);
            Assert.That(bus.Published.OfType<RoleCreatedForBots>().Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RoleCreated_RoleAlreadyGone_DispatchesNothing()
    {
        var bus = new FakeMessageBus();

        await RoleCreatedHandler.Handle(
            new RoleCreated { RoleId = "role-vanished", GuildId = GuildId },
            _context, _hub, PresenceIn(GuildId, (MemberId, UserId)), bus);

        Assert.Multiple(() =>
        {
            Assert.That(Clients.SentMessages, Is.Empty);
            Assert.That(bus.Published, Is.Empty);
        });
    }

    // ══════════════════════════════════════════════════════════════════════ RoleUpdatedHandler
    // dispatch - R15 ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task RoleUpdated_MetadataEdit_BroadcastsRoleUpdateAndNoMemberUpdate()
    {
        _context.Roles.Add(MakeRole());
        await _context.SaveChangesAsync();
        var bus = new FakeMessageBus();

        await RoleUpdatedHandler.Handle(
            new RoleUpdated { RoleId = RoleId, GuildId = GuildId, MemberId = null },
            _context, _service, bus, _hub, PresenceIn(GuildId, (MemberId, UserId)), Personas());

        Assert.Multiple(() =>
        {
            Assert.That(Clients.RecipientsOf("guild.RoleUpdated"), Is.EquivalentTo(new[] { UserId }));
            Assert.That(bus.Published.OfType<RoleUpdatedForBots>().Count(), Is.EqualTo(1));
            Assert.That(bus.Published.OfType<MemberUpdatedForBots>(), Is.Empty,
                "a role's own fields changing says nothing about any member");
        });
    }

    [Test]
    public async Task RoleUpdated_MembershipChange_BroadcastsMemberUpdateAndNoRoleUpdate()
    {
        _context.Roles.Add(MakeRole());
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();
        var bus = new FakeMessageBus();

        await RoleUpdatedHandler.Handle(
            new RoleUpdated { RoleId = RoleId, GuildId = GuildId, MemberId = MemberId },
            _context, _service, bus, _hub, PresenceIn(GuildId, (MemberId, UserId)), Personas());

        Assert.Multiple(() =>
        {
            Assert.That(Clients.RecipientsOf("guild.MemberRolesUpdated"), Is.EquivalentTo(new[] { UserId }));
            Assert.That(Clients.SentMessages.Any(m => m.Method == "guild.RoleUpdated"), Is.False);
            Assert.That(bus.Published.OfType<MemberUpdatedForBots>().Count(), Is.EqualTo(1));
            Assert.That(bus.Published.OfType<RoleUpdatedForBots>(), Is.Empty);
        });
    }

    // ══════════════════════════════════════════════════════════════════════ RoleDeletedHandler
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task RoleDeleted_InvalidatesEveryFormerHolder()
    {
        const string UserId2 = "user-2";

        // Post-delete DB state: no role row, no membership rows.
        var key1 = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        var key2 = GuildPermissionsForUser.GetCacheKey(GuildId, UserId2);
        _cache.SetEntry(key1, "stale-1");
        _cache.SetEntry(key2, "stale-2");

        await RoleDeletedHandler.Handle(
            new RoleDeleted { RoleId = RoleId, GuildId = GuildId, UserIds = [UserId, UserId2] },
            _service, _hub, EmptyPresence(), new FakeMessageBus(), Personas());

        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry(key1), Is.False);
            Assert.That(_cache.HasEntry(key2), Is.False);
        });
    }

    [Test]
    public async Task RoleDeleted_UnheldRole_LeavesOtherEntriesAlone()
    {
        var unrelated = GuildPermissionsForUser.GetCacheKey(GuildId, "user-9");
        _cache.SetEntry(unrelated, "fresh");

        await RoleDeletedHandler.Handle(
            new RoleDeleted { RoleId = RoleId, GuildId = GuildId, UserIds = [] },
            _service, _hub, EmptyPresence(), new FakeMessageBus(), Personas());

        Assert.That(_cache.HasEntry(unrelated), Is.True);
    }

    [Test]
    public async Task RoleDeleted_DuplicateHolders_AreInvalidatedOnce()
    {
        var key = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        _cache.SetEntry(key, "stale");

        await RoleDeletedHandler.Handle(
            new RoleDeleted { RoleId = RoleId, GuildId = GuildId, UserIds = [UserId, UserId] },
            _service, _hub, EmptyPresence(), new FakeMessageBus(), Personas());

        Assert.That(_cache.HasEntry(key), Is.False);
    }

    [Test]
    public async Task RoleDeleted_ThreeHolders_ProducesOneRoleEventAndNoMemberEvents()
    {
        var bus = new FakeMessageBus();

        await RoleDeletedHandler.Handle(
            new RoleDeleted { RoleId = RoleId, GuildId = GuildId, UserIds = ["user-a", "user-b", "user-c"] },
            _service, _hub, PresenceIn(GuildId, (MemberId, UserId)), bus, Personas());

        Assert.Multiple(() =>
        {
            Assert.That(bus.Published.OfType<RoleDeletedForBots>().Count(), Is.EqualTo(1),
                "one message about a role, never one per holder");
            Assert.That(bus.Published.OfType<MemberUpdatedForBots>(), Is.Empty);
            Assert.That(bus.Published, Has.Count.EqualTo(1));
            Assert.That(Clients.SentMessages.Count(m => m.Method == "guild.RoleDeleted"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RoleDeleted_OutsiderIsNotInTheAudience()
    {
        await RoleDeletedHandler.Handle(
            new RoleDeleted { RoleId = RoleId, GuildId = GuildId, UserIds = [UserId] },
            _service, _hub, PresenceIn("guild-elsewhere", ("member-x", "user-outsider")), new FakeMessageBus(),
            Personas());

        Assert.That(Clients.SentMessages, Is.Empty,
            "the audience is this guild's presence set; somebody who cannot see the guild is not in it");
    }

    // ══════════════════════════════════════════════════════════════════════
    // MemberRolesUpdatedHandler
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MemberRolesUpdated_InvalidatesTheMemberOnceAndTellsTheBots()
    {
        _context.GuildMembers.Add(MakeGuildMember());
        await _context.SaveChangesAsync();

        var key = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        _cache.SetEntry(key, "stale");
        var bus = new FakeMessageBus();

        await MemberRolesUpdatedHandler.Handle(
            new MemberRolesUpdated
            {
                GuildId = GuildId, MemberId = MemberId,
                AddedRoleIds = ["role-a", "role-b"], RemovedRoleIds = ["role-c"],
            },
            _context, _service, bus, _hub, PresenceIn(GuildId, (MemberId, UserId)), Personas());

        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry(key), Is.False);
            Assert.That(bus.Published, Has.Count.EqualTo(1),
                "one dispatch for the whole change, however many roles moved");
            Assert.That(Clients.RecipientsOf("guild.MemberRolesUpdated"), Is.EquivalentTo(new[] { UserId }));
        });
    }

    [Test]
    public async Task MemberRolesUpdated_UnknownMember_DoesNothing()
    {
        var bus = new FakeMessageBus();

        await MemberRolesUpdatedHandler.Handle(
            new MemberRolesUpdated { GuildId = GuildId, MemberId = "member-gone" },
            _context, _service, bus, _hub, PresenceIn(GuildId, (MemberId, UserId)), Personas());

        Assert.Multiple(() =>
        {
            Assert.That(bus.Published, Is.Empty);
            Assert.That(Clients.SentMessages, Is.Empty);
        });
    }
}
