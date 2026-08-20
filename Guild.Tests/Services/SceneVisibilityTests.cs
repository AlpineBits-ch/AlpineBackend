using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// Who can see a cast-only scene. The map lookup itself, and the two permission paths that decide
/// whether a private scene's messages ever leave the server: the read and the realtime fan-out.
/// </summary>
[TestFixture]
public class SceneVisibilityTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string GameMasterId = "user-gm";
    private const string CastPlayerId = "user-cast";
    private const string OutsiderId = "user-outsider";

    /// <summary>The second player on a shared guild character, who is cast by proxy.</summary>
    private const string SharedPlayerId = "user-shared";

    private const string ParentChannelId = "chan-ic";
    private const string SceneChannelId = "scene-1";
    private const string OocThreadId = "scene-1-ooc";
    private const string OpenSceneChannelId = "scene-open";

    private const string CastPersonaId = "pers_mayor";
    private const string OutsiderPersonaId = "pers_guard";
    private const string SharedPersonaId = "pers_chorus";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private PersonaService _personas = null!;
    private SceneVisibilityCache _visibility = null!;
    private GuildPermissionService _permissions = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _personas = new PersonaService(_cache, _context);
        _visibility = new SceneVisibilityCache(_cache, _context, _personas);
        _permissions = PermissionTestFactory.Create(_cache, _context, sceneVisibility: _visibility);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ══════════════════════════════════════════════════════════════════════ Seeding
    // ══════════════════════════════════════════════════════════════════════

    private async Task SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "Blackwater",
            Features = GuildFeatures.Scenes | GuildFeatures.Personas | GuildFeatures.Threads,
            CreatedAt = now, UpdatedAt = now,
        });

        _context.Roles.Add(new Role
        {
            Id = "role-everyone", GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone,
            Permissions = Permissions.ViewChannel | Permissions.ReadMessageHistory
                          | Permissions.SendMessages | Permissions.SendMessagesInThreads,
            CreatedAt = now, UpdatedAt = now,
        });

        _context.Roles.Add(new Role
        {
            Id = "role-gm", GuildId = GuildId, Name = "Gamemaster",
            ModulePermissions = ModulePermissions.ManageScenes,
            CreatedAt = now, UpdatedAt = now,
        });

        AddMember("memb-gm", GameMasterId);
        AddMember("memb-cast", CastPlayerId);
        AddMember("memb-outsider", OutsiderId);
        AddMember("memb-shared", SharedPlayerId);

        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-gm-role", RoleId = "role-gm", MemberId = "memb-gm",
            CreatedAt = now, UpdatedAt = now,
        });

        _context.Channels.Add(new Channel
        {
            Id = ParentChannelId, GuildId = GuildId, Name = "the-inn", Type = ChannelType.Text,
            CreatedAt = now, UpdatedAt = now,
        });

        AddPersona(CastPersonaId, "Mayor Cogsgrove", CastPlayerId);
        AddPersona(OutsiderPersonaId, "Town Guard", OutsiderId);

        // Guild-owned, granted to two players: the second one is in the cast by proxy and must see
        // the scene without ever having been named on it.
        _context.Set<Persona>().Add(new Persona
        {
            Id = SharedPersonaId, Scope = PersonaScope.Guild, OwnerGuildId = GuildId,
            Name = "The Chorus", CreatedAt = now, UpdatedAt = now,
        });

        _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
        {
            Id = $"profile-{SharedPersonaId}", PersonaId = SharedPersonaId, GuildId = GuildId,
            ApprovalState = PersonaApprovalState.Approved, CreatedAt = now, UpdatedAt = now,
        });

        _context.Set<PersonaGrant>().Add(new PersonaGrant
        {
            Id = "grant-chorus-shared", PersonaId = SharedPersonaId, UserId = SharedPlayerId,
            CreatedAt = now, UpdatedAt = now,
        });

        await _context.SaveChangesAsync();
        return;

        void AddMember(string memberId, string userId)
        {
            _context.GuildMembers.Add(new GuildMember
            {
                Id = memberId, GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
                CreatedAt = now, UpdatedAt = now, SearchValue = $"{userId}#{GuildId}",
            });

            _context.RoleMembers.Add(new RoleMember
            {
                Id = $"rm-{memberId}", RoleId = "role-everyone", MemberId = memberId,
                CreatedAt = now, UpdatedAt = now,
            });
        }

        void AddPersona(string personaId, string name, string ownerUserId)
        {
            _context.Set<Persona>().Add(new Persona
            {
                Id = personaId, Scope = PersonaScope.User, OwnerUserId = ownerUserId, Name = name,
                CreatedAt = now, UpdatedAt = now,
            });

            _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
            {
                Id = $"profile-{personaId}", PersonaId = personaId, GuildId = GuildId,
                ApprovalState = PersonaApprovalState.Approved, CreatedAt = now, UpdatedAt = now,
            });
        }
    }

    private async Task<SceneState> SeedSceneAsync(
        string channelId, SceneVisibility visibility, List<string> cast, string? oocThreadId = null)
    {
        var now = DateTimeOffset.UtcNow;

        _context.Channels.Add(new Channel
        {
            Id = channelId, GuildId = GuildId, Name = "The Siege of Blackwater",
            Type = ChannelType.Scene, ParentChannelId = ParentChannelId,
            CreatedByUserId = GameMasterId, CreatedAt = now, UpdatedAt = now,
        });

        if (oocThreadId is not null)
        {
            _context.Channels.Add(new Channel
            {
                Id = oocThreadId, GuildId = GuildId, Name = "The Siege of Blackwater (OOC)",
                Type = ChannelType.Thread, ParentChannelId = ParentChannelId,
                CreatedByUserId = GameMasterId, CreatedAt = now, UpdatedAt = now,
            });
        }

        var state = SceneState.Create(new CreateSceneStateParams
        {
            ChannelId = channelId, GuildId = GuildId, OocThreadId = oocThreadId,
            ParticipantPersonaIds = cast,
            JoinPolicy = visibility == SceneVisibility.Cast ? SceneJoinPolicy.Ask : SceneJoinPolicy.Open,
            Visibility = visibility,
        });

        _context.Set<SceneState>().Add(state);
        await _context.SaveChangesAsync();

        return state;
    }

    // ══════════════════════════════════════════════════════════════════════ The four branches
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ACastOnlyScene_IsInvisibleToSomebodyWithNobodyInIt()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneChannelId, SceneVisibility.Cast, [CastPersonaId]);

        Assert.That(
            await _visibility.CanSeeAsync(OutsiderId, GuildId, SceneChannelId, isGameMaster: false),
            Is.False);
    }

    [Test]
    public async Task ACastOnlyScene_IsVisibleToWhoeverAnswersForOneOfItsCharacters()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneChannelId, SceneVisibility.Cast, [CastPersonaId]);

        Assert.That(
            await _visibility.CanSeeAsync(CastPlayerId, GuildId, SceneChannelId, isGameMaster: false),
            Is.True);
    }

    [Test]
    public async Task ACastOnlyScene_IsVisibleToAGameMasterWhoIsNotInIt()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneChannelId, SceneVisibility.Cast, [CastPersonaId]);

        Assert.That(
            await _visibility.CanSeeAsync(GameMasterId, GuildId, SceneChannelId, isGameMaster: true),
            Is.True);
    }

    [Test]
    public async Task AnEveryoneScene_IsNotInTheMapAtAll()
    {
        await SeedAsync();
        await SeedSceneAsync(OpenSceneChannelId, SceneVisibility.Everyone, [CastPersonaId]);

        var restricted = await _visibility.RestrictedAsync(GuildId);

        Assert.Multiple(async () =>
        {
            Assert.That(restricted, Is.Empty);
            Assert.That(
                await _visibility.CanSeeAsync(
                    OutsiderId, GuildId, OpenSceneChannelId, isGameMaster: false),
                Is.True);
        });
    }

    /// <summary>
    /// The case the whole shared-character model turns on: the cast names a guild character, and
    /// the second player holding a grant on it was never named anywhere.
    /// </summary>
    [Test]
    public async Task ASharedCharacterInTheCast_LetsItsSecondPlayerIn()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneChannelId, SceneVisibility.Cast, [SharedPersonaId]);

        Assert.Multiple(async () =>
        {
            Assert.That(
                await _visibility.CanSeeAsync(
                    SharedPlayerId, GuildId, SceneChannelId, isGameMaster: false),
                Is.True);

            Assert.That(
                await _visibility.CanSeeAsync(
                    OutsiderId, GuildId, SceneChannelId, isGameMaster: false),
                Is.False);
        });
    }

    [Test]
    public async Task TheOutOfCharacterThread_FollowsTheScenesVisibility()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneChannelId, SceneVisibility.Cast, [CastPersonaId], OocThreadId);

        Assert.Multiple(async () =>
        {
            Assert.That(
                await _visibility.CanSeeAsync(OutsiderId, GuildId, OocThreadId, isGameMaster: false),
                Is.False);

            Assert.That(
                await _visibility.CanSeeAsync(CastPlayerId, GuildId, OocThreadId, isGameMaster: false),
                Is.True);
        });
    }

    [Test]
    public async Task AddingACharacterToTheCast_LetsItsPlayerIn()
    {
        await SeedAsync();
        var state = await SeedSceneAsync(SceneChannelId, SceneVisibility.Cast, [CastPersonaId]);

        Assert.That(
            await _visibility.CanSeeAsync(OutsiderId, GuildId, SceneChannelId, isGameMaster: false),
            Is.False);

        state.AddParticipant(OutsiderPersonaId, DateTimeOffset.UtcNow);
        await _context.SaveChangesAsync();
        await _visibility.InvalidateGuildAsync(GuildId);

        Assert.That(
            await _visibility.CanSeeAsync(OutsiderId, GuildId, SceneChannelId, isGameMaster: false),
            Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════ The permission paths
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ViewChannel_IsRefusedOnACastOnlyScene()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneChannelId, SceneVisibility.Cast, [CastPersonaId]);

        Assert.Multiple(async () =>
        {
            Assert.That(
                await _permissions.CanUserPerformActionAsync(
                    OutsiderId, SceneChannelId, Permissions.ViewChannel),
                Is.False);

            Assert.That(
                await _permissions.CanUserPerformActionAsync(
                    CastPlayerId, SceneChannelId, Permissions.ViewChannel),
                Is.True);
        });
    }

    /// <summary>
    /// The test that proves a private scene's messages do not reach other people's sockets: the
    /// realtime fan-out and the bot dispatch both resolve their audience through this.
    /// </summary>
    [Test]
    public async Task FanOut_DropsAMemberWithNobodyInTheScene()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneChannelId, SceneVisibility.Cast, [CastPersonaId]);

        var allowed = await _permissions.FilterUsersWithChannelPermissionAsync(
            SceneChannelId, [CastPlayerId, OutsiderId, GameMasterId, OwnerId],
            Permissions.ViewChannel);

        Assert.Multiple(() =>
        {
            Assert.That(allowed, Does.Not.Contain(OutsiderId));
            Assert.That(allowed, Does.Contain(CastPlayerId));
            Assert.That(allowed, Does.Contain(GameMasterId));
            Assert.That(allowed, Does.Contain(OwnerId), "the guild owner keeps seeing everything");
        });
    }

    [Test]
    public async Task ChannelLists_DropACastOnlySceneAndKeepAnOpenOne()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneChannelId, SceneVisibility.Cast, [CastPersonaId]);
        await SeedSceneAsync(OpenSceneChannelId, SceneVisibility.Everyone, [CastPersonaId]);

        var forOutsider = await _permissions.FilterChannelsWithPermissionAsync(
            OutsiderId, GuildId, [SceneChannelId, OpenSceneChannelId, ParentChannelId],
            Permissions.ViewChannel);

        var forCast = await _permissions.FilterChannelsWithPermissionAsync(
            CastPlayerId, GuildId, [SceneChannelId, OpenSceneChannelId, ParentChannelId],
            Permissions.ViewChannel);

        Assert.Multiple(() =>
        {
            Assert.That(forOutsider, Does.Not.Contain(SceneChannelId));
            Assert.That(forOutsider, Does.Contain(OpenSceneChannelId));
            Assert.That(forOutsider, Does.Contain(ParentChannelId));
            Assert.That(forCast, Does.Contain(SceneChannelId));
        });
    }

    [Test]
    public async Task TheGuildOwner_KeepsSeeingACastOnlyScene()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneChannelId, SceneVisibility.Cast, [CastPersonaId]);

        Assert.Multiple(async () =>
        {
            Assert.That(
                await _permissions.CanUserPerformActionAsync(
                    OwnerId, SceneChannelId, Permissions.ViewChannel),
                Is.True);

            Assert.That(
                await _permissions.FilterChannelsWithPermissionAsync(
                    OwnerId, GuildId, [SceneChannelId], Permissions.ViewChannel),
                Does.Contain(SceneChannelId));
        });
    }
}
