using Guild.Application.Dtos.Request;
using Guild.Application.Endpoints.Channel;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ChannelDto = Guild.Application.Dtos.Response.ChannelDto;

namespace Guild.Tests.Endpoints;

/// <summary>
/// The channel settings privacy switch end to end against a real Postgres, committed the way the
/// Wolverine middleware commits it.
/// </summary>
[TestFixture]
public class ChannelUpdatePrivacyPostgresTests
{
    private const string GuildId = "guild-upd-pg";
    private const string UserId = "user-upd-pg";
    private const string MemberId = "member-upd-pg";
    private const string ManagerRoleId = "role-manager-pg";
    private const string EveryoneRoleId = "role-everyone-upd-pg";

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private MicroserviceContext _context = null!;
    private Channel _channel = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync();
        _context = new PostgresGuildContext();

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, Name = "g", OwnerId = UserId, CreatedAt = Now, UpdatedAt = Now,
        });
        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone,
            Permissions = Permissions.ViewChannel, CreatedAt = Now, UpdatedAt = Now,
        });
        _context.Roles.Add(new Role
        {
            Id = ManagerRoleId, GuildId = GuildId, Name = "manager",
            Permissions = Permissions.ManageChannel | Permissions.ViewChannel, CreatedAt = Now, UpdatedAt = Now,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = Now, UpdatedAt = Now, SearchValue = $"{UserId}#{GuildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-upd-pg", RoleId = ManagerRoleId, MemberId = MemberId, CreatedAt = Now, UpdatedAt = Now,
        });
        _channel = Channel.Create(new CreateChannelParams
        {
            Name = "general", Type = ChannelType.Text, GuildId = GuildId, Description = "about",
        });
        _channel.SlowModeSeconds = 10;
        _context.Channels.Add(_channel);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await PostgresTestDatabase.ResetAsync();

    private async Task<IResult> PatchAsync(UpdateChannelDto dto)
    {
        await using var ctx = new PostgresGuildContext();
        var cache = new FakeDistributedCache();
        var result = await new ChannelEndpoint().UpdateChannelAsync(_channel.Id, dto,
            PermissionTestFactory.Create(cache, ctx), ctx, new FakeHubContext(),
            new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance),
            new AuditLogService(ctx), new ChannelPrivacyService(ctx), new FakeMessageBus(),
            TestPrincipal.Create(UserId));
        await ctx.SaveChangesAsync();
        return result;
    }

    private UpdateChannelDto Body(bool isPrivate) => new()
    {
        Name = "general", Description = "about", SlowModeSeconds = 10, IsPrivate = isPrivate,
    };

    [Test]
    public async Task MissingName_IsAValidationProblem()
    {
        var result = await PatchAsync(new UpdateChannelDto { IsPrivate = true });

        Assert.That(result, Is.InstanceOf<ProblemHttpResult>());
        var saved = await _context.Channels.AsNoTracking().FirstAsync(c => c.Id == _channel.Id);
        Assert.That(saved.IsPrivate, Is.False);
    }

    [Test]
    public async Task MakingPrivate_WritesTheEveryoneDenyAndAnswersWithIt()
    {
        var result = await PatchAsync(Body(true));

        var ok = result as Ok<ChannelDto>;
        Assert.That(ok, Is.Not.Null);
        Assert.That(ok!.Value!.IsPrivate, Is.True);
        Assert.That(ok.Value.Permissions.Single(p => p.RoleId == EveryoneRoleId).DenyPermissions,
            Is.EqualTo(Permissions.ViewChannel));

        var saved = await _context.Channels.AsNoTracking().FirstAsync(c => c.Id == _channel.Id);
        Assert.That(saved.IsPrivate, Is.True);
        var row = await _context.ChannelPermissions.AsNoTracking()
            .SingleAsync(p => p.ChannelId == _channel.Id && p.RoleId == EveryoneRoleId);
        Assert.That(row.DenyPermissions, Is.EqualTo(Permissions.ViewChannel));
    }

    [Test]
    public async Task PrivateThenPublicThenPrivate_RewritesAnEveryoneRowCarryingOtherBits()
    {
        _context.ChannelPermissions.Add(new ChannelPermission
        {
            Id = "chpr-upd-pg", ChannelId = _channel.Id, RoleId = EveryoneRoleId,
            AllowPermissions = Permissions.None, DenyPermissions = Permissions.SendMessages,
            CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        Assert.That(await PatchAsync(Body(true)), Is.InstanceOf<Ok<ChannelDto>>());
        Assert.That(await PatchAsync(Body(false)), Is.InstanceOf<Ok<ChannelDto>>());
        Assert.That(await PatchAsync(Body(true)), Is.InstanceOf<Ok<ChannelDto>>());

        var row = await _context.ChannelPermissions.AsNoTracking()
            .SingleAsync(p => p.ChannelId == _channel.Id && p.RoleId == EveryoneRoleId);
        Assert.That(row.DenyPermissions, Is.EqualTo(Permissions.SendMessages | Permissions.ViewChannel));
    }
}
