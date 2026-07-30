using Guild.Application.Dtos.Request;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers WelcomeScreenEndpoint: ManageGuild-gated writes, the caps and cross-guild channel checks,
/// and that reads are membership-gated (the pre-join case is served by the invite preview instead).
/// </summary>
[TestFixture]
public class WelcomeScreenEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";
    private const string ChannelId = "chan-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private AuditLogService _auditLog = null!;
    private WelcomeScreenEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _auditLog = new AuditLogService(_context);
        _endpoint = new WelcomeScreenEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedManagerMember(Permissions permissions = Permissions.ManageGuild)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Name = "manager", Permissions = permissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = RoleId, MemberId = MemberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "welcome", Type = ChannelType.Text,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    private static UpdateWelcomeScreenDto Valid(int channelCount = 1) => new()
    {
        Enabled = true,
        Description = "A friendly place",
        Channels = Enumerable.Range(0, channelCount)
            .Select(i => new WelcomeChannelDto { ChannelId = ChannelId, Description = $"start here {i}" })
            .ToList(),
    };

    private Task<IResult> Put(UpdateWelcomeScreenDto dto) =>
        _endpoint.Update(GuildId, dto, _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));

    [Test]
    public async Task Update_LacksManageGuild_ReturnsForbid()
    {
        await SeedManagerMember(Permissions.ViewChannel);

        Assert.That(await Put(Valid()), Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Update_Valid_PersistsScreenAndChannels()
    {
        await SeedManagerMember();

        var result = await Put(Valid());
        await _context.SaveChangesAsync();

        var stored = _context.Set<GuildWelcomeScreen>().Find(GuildId);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<UpdateWelcomeScreenDto>>());
            Assert.That(stored, Is.Not.Null);
            Assert.That(stored!.Enabled, Is.True);
            Assert.That(_context.Set<GuildWelcomeChannel>().Count(c => c.GuildId == GuildId), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Update_ReplacesPreviousChannels()
    {
        await SeedManagerMember();
        await Put(Valid());
        await _context.SaveChangesAsync();

        await Put(new UpdateWelcomeScreenDto { Enabled = true, Channels = [] });
        await _context.SaveChangesAsync();

        Assert.That(_context.Set<GuildWelcomeChannel>().Count(c => c.GuildId == GuildId), Is.Zero,
            "PUT replaces the channel list wholesale");
    }

    [Test]
    public async Task Update_MoreThanFiveChannels_ReturnsBadRequest()
    {
        await SeedManagerMember();

        Assert.That(await Put(Valid(channelCount: 6)), Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Update_DuplicateChannel_ReturnsBadRequest()
    {
        await SeedManagerMember();

        Assert.That(await Put(Valid(channelCount: 2)), Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Update_ChannelFromAnotherGuild_ReturnsBadRequest()
    {
        await SeedManagerMember();
        _context.Channels.Add(new Channel
        {
            Id = "chan-elsewhere", GuildId = "other-guild", Name = "nope", Type = ChannelType.Text,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var dto = new UpdateWelcomeScreenDto
        {
            Enabled = true,
            Channels = [new WelcomeChannelDto { ChannelId = "chan-elsewhere", Description = "hi" }],
        };

        Assert.That(await Put(dto), Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Update_OversizeDescription_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var dto = Valid();
        dto.Description = new string('x', 141);

        Assert.That(await Put(dto), Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Get_NotAMember_ReturnsNotFound()
    {
        await SeedManagerMember();

        var result = await _endpoint.Get(GuildId, _context, TestPrincipal.Create("stranger"));

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task LoadAsync_EnabledOnly_HidesDisabledScreenFromInvitePreview()
    {
        await SeedManagerMember();
        var dto = Valid();
        dto.Enabled = false;
        await Put(dto);
        await _context.SaveChangesAsync();

        var forPreview = await WelcomeScreenEndpoint.LoadAsync(_context, GuildId, enabledOnly: true);

        Assert.That(forPreview, Is.Null, "an invite must not advertise a welcome screen that is switched off");
    }
}
