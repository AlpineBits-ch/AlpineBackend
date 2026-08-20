using Amazon.S3;
using Guild.Application.Controllers;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Services;

namespace Guild.Tests.Controllers;

/// <summary>Paging the guild member list, which every client does past fifty members.</summary>
[TestFixture]
public class GuildMemberPagingTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string EveryoneRoleId = "role-everyone";
    private const int PageSize = 50;

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();

        var bus = new FakeInvokingMessageBus();
        bus.SetResponse<GetProfileByUserIdsRequest>(new GetProfileByUserIdsResponse { Profiles = [] });

        _controller = new GuildController(
            _context,
            new GuildThumbnailService(Substitute.For<IAmazonS3>()),
            PermissionTestFactory.Create(_cache, _context),
            NullLogger<GuildController>.Instance,
            new ProfileService(bus, _cache),
            new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance),
            bus,
            PrivacyTestFactory.Privacy(bus, _cache))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = TestPrincipal.Create(OwnerId) },
            },
        };
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>Names are ordinal so the assertion can name the row it expects.</summary>
    private async Task SeedAsync(int memberCount)
    {
        var now = DateTimeOffset.UtcNow;

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "g", CreatedAt = now, UpdatedAt = now,
        });
        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone,
            Permissions = Permissions.ViewChannel, CreatedAt = now, UpdatedAt = now,
        });

        for (var i = 0; i < memberCount; i++)
        {
            var memberId = $"member-{i:D3}";
            _context.GuildMembers.Add(new GuildMember
            {
                Id = memberId, GuildId = GuildId, UserId = $"user-{i:D3}", Nickname = $"n{i:D3}",
                JoinedAt = DateTime.UtcNow, SearchValue = $"USER-{i:D3}",
                CreatedAt = now.AddSeconds(i), UpdatedAt = now,
            });
            _context.RoleMembers.Add(new RoleMember
            {
                Id = $"rm-{i:D3}", RoleId = EveryoneRoleId, MemberId = memberId,
                CreatedAt = now, UpdatedAt = now,
            });
        }

        // The caller is the owner, who needs a member row of their own for nothing here but is the
        // one the permission gate resolves.
        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-owner", GuildId = GuildId, UserId = OwnerId, Nickname = "owner",
            JoinedAt = DateTime.UtcNow, SearchValue = "OWNER-1",
            CreatedAt = now.AddSeconds(memberCount), UpdatedAt = now,
        });

        await _context.SaveChangesAsync();
    }

    private async Task<List<MemberDto>> PageAsync(int skip, int take)
    {
        var result = await _controller.GetGuildMembers(GuildId, skip, take);
        var ok = result as OkObjectResult;

        Assert.That(ok, Is.Not.Null);
        return (List<MemberDto>)ok!.Value!;
    }

    [Test]
    public async Task TheSecondPage_HoldsTheSecondFiftyRatherThanNothing()
    {
        await SeedAsync(120);

        var second = await PageAsync(PageSize, PageSize);

        Assert.That(second, Has.Count.EqualTo(PageSize));
        Assert.That(second[0].Id, Is.EqualTo("member-050"));
    }

    [Test]
    public async Task TheFirstPage_StillHoldsTheFirstFifty()
    {
        await SeedAsync(120);

        var first = await PageAsync(0, PageSize);

        Assert.That(first, Has.Count.EqualTo(PageSize));
        Assert.That(first[0].Id, Is.EqualTo("member-000"));
    }

    [Test]
    public async Task TheLastPage_HoldsWhatIsLeft()
    {
        await SeedAsync(120);

        var third = await PageAsync(PageSize * 2, PageSize);

        // 120 seeded members plus the owner's own row.
        Assert.That(third, Has.Count.EqualTo(21));
        Assert.That(third[0].Id, Is.EqualTo("member-100"));
    }
}
