using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Who can actually see a channel.
///
/// <para>This exists for end-to-end encryption, which needs the exact roster: anyone handed group
/// keys can read the traffic, so a list that is too generous is a confidentiality bug rather than
/// a cosmetic one. These tests are mostly about the <i>too generous</i> direction.</para>
/// </summary>
[TestFixture]
public class ChannelViewersEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string ChannelId = "channel-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _service = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedGuild(Permissions everyonePermissions)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "g",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "c", Description = "d", Type = ChannelType.Text,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = "role-everyone", GuildId = GuildId, Name = "everyone", Permissions = everyonePermissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    private async Task SeedMember(string userId, string memberId, string? roleId = "role-everyone")
    {
        _context.GuildMembers.Add(new GuildMember
        {
            Id = memberId, GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = userId.ToUpperInvariant(),
        });
        if (roleId is not null)
        {
            _context.RoleMembers.Add(new RoleMember
            {
                Id = "rm-" + memberId, RoleId = roleId, MemberId = memberId,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        await _context.SaveChangesAsync();
    }

    private Task<IResult> Get(string asUser) =>
        ChannelViewersEndpoint.GetChannelViewers(ChannelId, _service, _context, TestPrincipal.Create(asUser));

    private static List<string> ViewersOf(IResult result) =>
        ((Ok<ChannelViewersDto>)result).Value!.UserIds;

    [Test]
    public async Task Unauthenticated_ReturnsUnauthorized()
    {
        await SeedGuild(Permissions.ViewChannel);

        var result = await ChannelViewersEndpoint.GetChannelViewers(
            ChannelId, _service, _context, TestPrincipal.CreateAnonymous());

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task UnknownChannel_ReturnsNotFound()
    {
        await SeedGuild(Permissions.ViewChannel);

        var result = await ChannelViewersEndpoint.GetChannelViewers(
            "no-such-channel", _service, _context, TestPrincipal.Create(OwnerId));

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task CallerWhoCannotSeeTheChannel_ReturnsForbidden()
    {
        await SeedGuild(Permissions.None);
        await SeedMember("user-1", "member-1");

        // You have to be able to see the channel to ask who else can.
        Assert.That(await Get("user-1"), Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task ListsMembersWhoHoldViewChannel()
    {
        await SeedGuild(Permissions.ViewChannel);
        await SeedMember("user-1", "member-1");
        await SeedMember("user-2", "member-2");

        var viewers = ViewersOf(await Get("user-1"));

        Assert.That(viewers, Does.Contain("user-1").And.Contain("user-2"));
    }

    [Test]
    public async Task ExcludesMembersWithoutViewChannel()
    {
        await SeedGuild(Permissions.ViewChannel);
        await SeedMember("user-1", "member-1");
        // No role at all, so nothing grants them ViewChannel.
        await SeedMember("user-outsider", "member-outsider", roleId: null);

        var viewers = ViewersOf(await Get("user-1"));

        // This is the case the guild-member-list fallback got wrong: it would have handed group
        // keys to someone who cannot open the channel at all.
        Assert.That(viewers, Does.Not.Contain("user-outsider"));
    }

    [Test]
    public async Task RespectsAChannelOverwriteThatDeniesView()
    {
        await SeedGuild(Permissions.ViewChannel);
        await SeedMember("user-1", "member-1");
        await SeedMember("user-denied", "member-denied");

        _context.ChannelPermissions.Add(new ChannelPermission
        {
            Id = "cp-1",
            ChannelId = ChannelId,
            MemberId = "member-denied",
            DenyPermissions = Permissions.ViewChannel,
            AllowPermissions = Permissions.None,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var viewers = ViewersOf(await Get("user-1"));

        // A restrictive overwrite is exactly the case where "everyone in the guild" is wrong.
        Assert.That(viewers, Does.Not.Contain("user-denied"));
        Assert.That(viewers, Does.Contain("user-1"));
    }

    [Test]
    public async Task IncludesTheOwnerEvenWithoutAnExplicitRole()
    {
        await SeedGuild(Permissions.ViewChannel);
        await SeedMember(OwnerId, "member-owner", roleId: null);
        await SeedMember("user-1", "member-1");

        var viewers = ViewersOf(await Get("user-1"));

        // Leaving the owner out would build a group the owner cannot read.
        Assert.That(viewers, Does.Contain(OwnerId));
    }
}
