using Guild.Application.Endpoints.Channel;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Guild.Tests.Endpoints;

/// <summary>What a subject ends up with in a channel, and which layer said so.</summary>
[TestFixture]
public class EffectivePermissionsEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string ChannelId = "chan-1";
    private const string EveryoneRoleId = "role-everyone";
    private const string PlayerRoleId = "role-player";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _service = PermissionTestFactory.Create(_cache, _context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "g", CreatedAt = now, UpdatedAt = now,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "c", Description = "d", Type = ChannelType.Text,
            CreatedAt = now, UpdatedAt = now,
        });
        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone,
            Permissions = Permissions.ViewChannel | Permissions.SendMessages,
            CreatedAt = now, UpdatedAt = now,
        });
        _context.Roles.Add(new Role
        {
            Id = PlayerRoleId, GuildId = GuildId, Name = "player", Type = RoleType.None,
            Permissions = Permissions.None, CreatedAt = now, UpdatedAt = now,
        });
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task Owner_GetsTheTraceForARole()
    {
        await SeedAsync();

        var result = await EffectivePermissionsEndpoint.GetEffectivePermissions(
            ChannelId, PlayerRoleId, null, _service, _context, TestPrincipal.Create(OwnerId));

        var ok = result as Ok<EffectivePermissionsDto>;
        Assert.That(ok, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(ok!.Value!.SubjectKind, Is.EqualTo(nameof(PermissionSubjectKind.Role)));
            Assert.That(ok.Value.SubjectId, Is.EqualTo(PlayerRoleId));
            Assert.That(ok.Value.Sources.Any(s => s.Permission == nameof(Permissions.ViewChannel) && s.Granted), Is.True);
        });
    }

    [Test]
    public async Task BothSubjects_IsABadRequest()
    {
        await SeedAsync();

        var result = await EffectivePermissionsEndpoint.GetEffectivePermissions(
            ChannelId, PlayerRoleId, "member-1", _service, _context, TestPrincipal.Create(OwnerId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task NeitherSubject_IsABadRequest()
    {
        await SeedAsync();

        var result = await EffectivePermissionsEndpoint.GetEffectivePermissions(
            ChannelId, null, null, _service, _context, TestPrincipal.Create(OwnerId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task WithoutManagePermissions_IsForbidden()
    {
        await SeedAsync();
        var now = DateTimeOffset.UtcNow;

        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-1", GuildId = GuildId, UserId = "user-1", JoinedAt = DateTime.UtcNow,
            SearchValue = "USER-1", CreatedAt = now, UpdatedAt = now,
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = EveryoneRoleId, MemberId = "member-1", CreatedAt = now, UpdatedAt = now,
        });
        await _context.SaveChangesAsync();

        var result = await EffectivePermissionsEndpoint.GetEffectivePermissions(
            ChannelId, PlayerRoleId, null, _service, _context, TestPrincipal.Create("user-1"));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UnknownChannel_IsNotFound()
    {
        await SeedAsync();

        var result = await EffectivePermissionsEndpoint.GetEffectivePermissions(
            "nope", PlayerRoleId, null, _service, _context, TestPrincipal.Create(OwnerId));

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    /// <summary>Every channel-scoped permission is reported, granted or not, so the UI never guesses.</summary>
    [Test]
    public async Task Sources_CoverEveryChannelScopedPermission()
    {
        await SeedAsync();

        var result = await EffectivePermissionsEndpoint.GetEffectivePermissions(
            ChannelId, PlayerRoleId, null, _service, _context, TestPrincipal.Create(OwnerId));

        var ok = result as Ok<EffectivePermissionsDto>;
        Assert.That(ok, Is.Not.Null);

        string[] expected =
        [
            nameof(Permissions.ViewChannel), nameof(Permissions.CreateInvite), nameof(Permissions.UseApplicationCommands),
            nameof(Permissions.SendMessages), nameof(Permissions.ReadMessageHistory), nameof(Permissions.EditOwnMessages),
            nameof(Permissions.EditAnyMessage), nameof(Permissions.DeleteOwnMessages), nameof(Permissions.DeleteAnyMessage),
            nameof(Permissions.PinMessages), nameof(Permissions.MentionEveryone),
            nameof(Permissions.AttachFiles), nameof(Permissions.EmbedLinks), nameof(Permissions.AddReactions), nameof(Permissions.UseExternalEmojis),
            nameof(Permissions.Connect), nameof(Permissions.Speak), nameof(Permissions.Stream),
            nameof(Permissions.MuteMembers), nameof(Permissions.DeafenMembers), nameof(Permissions.MoveMembers),
            nameof(Permissions.CreateThreads), nameof(Permissions.SendMessagesInThreads),
            nameof(Permissions.ManageOwnThreads), nameof(Permissions.ManageAnyThread),
            nameof(Permissions.ManageChannel), nameof(Permissions.ManagePermissions), nameof(Permissions.ManageWebhooks),
        ];

        Assert.That(ok!.Value!.Sources.Select(s => s.Permission), Is.EqualTo(expected));
    }
}
