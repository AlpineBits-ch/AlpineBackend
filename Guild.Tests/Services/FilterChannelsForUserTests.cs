using Guild.Application.Bus.Consumers;
using Guild.Application.Services;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// The one-user-many-channels filter and the bus contract in front of it, which exist so that a
/// batched read on another service can authorize a whole page in one round-trip instead of one
/// request per item.
/// </summary>
[TestFixtureSource(typeof(GuildContextProviders))]
public class FilterChannelsForUserTests(IGuildContextProvider provider)
{
    private const string OwnerId = "user-owner";
    private const string UserId = "user-1";

    private const string GuildA = "guld-a";
    private const string GuildB = "guld-b";

    private const string OpenChannelA = "chan-a1";
    private const string PrivateChannelA = "chan-a2";
    private const string OpenChannelB = "chan-b1";

    private MicroserviceContext _context = null!;
    private GuildPermissionService _service = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = await provider.CreateAsync();
        _service = PermissionTestFactory.Create(new FakeDistributedCache(), _context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    /// <summary>Two guilds the same user belongs to, three channels between them. @everyone is
    /// implicit (no RoleMember rows), which is how every member holds it since R12.</summary>
    private async Task SeedAsync()
    {
        foreach (var (guildId, channelIds) in new[]
                 {
                     (GuildA, new[] { OpenChannelA, PrivateChannelA }),
                     (GuildB, new[] { OpenChannelB }),
                 })
        {
            _context.Guilds.Add(new global::Guild.Domain.Aggregates.Guild
            {
                Id = guildId, Name = guildId, OwnerId = OwnerId, CreatedAt = Now, UpdatedAt = Now,
            });

            _context.Roles.Add(new Role
            {
                Id = $"role-everyone-{guildId}", GuildId = guildId, Name = Role.EveryoneRoleName,
                Type = RoleType.Everyone, Position = 0,
                Permissions = Role.DefaultEveryonePermissions,
                CreatedAt = Now, UpdatedAt = Now,
            });

            foreach (var channelId in channelIds)
            {
                _context.Channels.Add(new Channel
                {
                    Id = channelId, GuildId = guildId, Name = channelId, Description = "d",
                    Type = ChannelType.Text, CreatedAt = Now, UpdatedAt = Now,
                });
            }

            _context.GuildMembers.Add(new GuildMember
            {
                Id = $"memb-{guildId}", GuildId = guildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
                SearchValue = UserId, CreatedAt = Now, UpdatedAt = Now,
            });
        }

        await _context.SaveChangesAsync();
    }

    private async Task DenyAsync(string channelId, string guildId, Permissions deny)
    {
        _context.Set<ChannelPermission>().Add(new ChannelPermission
        {
            Id = $"chpr-{channelId}-{(long)deny}", ChannelId = channelId, RoleId = $"role-everyone-{guildId}",
            AllowPermissions = Permissions.None, DenyPermissions = deny, CreatedAt = Now, UpdatedAt = Now,
        });

        await _context.SaveChangesAsync();
    }

    // ── Normal ────────────────────────────────────────────────────────────────

    [Test]
    public async Task SpansGuilds_AndReturnsEveryVisibleChannel()
    {
        await SeedAsync();

        var allowed = await _service.FilterChannelsWithPermissionAsync(
            UserId, [OpenChannelA, PrivateChannelA, OpenChannelB], Permissions.ViewChannel);

        Assert.That(allowed, Is.EquivalentTo(new[] { OpenChannelA, PrivateChannelA, OpenChannelB }));
    }

    [Test]
    public async Task Owner_HoldsEverythingInBothGuilds()
    {
        await SeedAsync();

        var allowed = await _service.FilterChannelsWithPermissionAsync(
            OwnerId, [OpenChannelA, PrivateChannelA, OpenChannelB], Permissions.ViewChannel);

        Assert.That(allowed, Is.EquivalentTo(new[] { OpenChannelA, PrivateChannelA, OpenChannelB }));
    }

    // ── Deny ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task ViewChannelDeny_DropsOnlyThatChannel()
    {
        await SeedAsync();
        await DenyAsync(PrivateChannelA, GuildA, Permissions.ViewChannel);

        var allowed = await _service.FilterChannelsWithPermissionAsync(
            UserId, [OpenChannelA, PrivateChannelA, OpenChannelB], Permissions.ViewChannel);

        Assert.That(allowed, Is.EquivalentTo(new[] { OpenChannelA, OpenChannelB }),
            "the filter is per channel - a deny in one guild must not take the other guild's page with it");
    }

    [Test]
    public async Task ReadMessageHistoryDeny_IsIndependentOfVisibility()
    {
        // The two bits have no edge between them in the implication table, which is exactly why the
        // batched history check asks both questions rather than one.
        await SeedAsync();
        await DenyAsync(OpenChannelB, GuildB, Permissions.ReadMessageHistory);

        var visible = await _service.FilterChannelsWithPermissionAsync(
            UserId, [OpenChannelA, OpenChannelB], Permissions.ViewChannel);
        var readable = await _service.FilterChannelsWithPermissionAsync(
            UserId, [OpenChannelA, OpenChannelB], Permissions.ReadMessageHistory);

        Assert.Multiple(() =>
        {
            Assert.That(visible, Is.EquivalentTo(new[] { OpenChannelA, OpenChannelB }));
            Assert.That(readable, Is.EquivalentTo(new[] { OpenChannelA }));
        });
    }

    [Test]
    public async Task NonMember_GetsNothing()
    {
        await SeedAsync();

        var allowed = await _service.FilterChannelsWithPermissionAsync(
            "user-stranger", [OpenChannelA, OpenChannelB], Permissions.ViewChannel);

        Assert.That(allowed, Is.Empty);
    }

    // ── Edge ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task UnknownChannelIds_AreDroppedRatherThanResolved()
    {
        await SeedAsync();

        var allowed = await _service.FilterChannelsWithPermissionAsync(
            UserId, [OpenChannelA, "chan-does-not-exist", ""], Permissions.ViewChannel);

        Assert.That(allowed, Is.EquivalentTo(new[] { OpenChannelA }));
    }

    [Test]
    public async Task EmptyInput_ReturnsEmpty()
    {
        await SeedAsync();

        Assert.That(await _service.FilterChannelsWithPermissionAsync(UserId, [], Permissions.ViewChannel), Is.Empty);
        Assert.That(await _service.FilterChannelsWithPermissionAsync("", [OpenChannelA], Permissions.ViewChannel), Is.Empty);
    }

    [Test]
    public async Task DuplicateIds_AreCollapsed()
    {
        await SeedAsync();

        var allowed = await _service.FilterChannelsWithPermissionAsync(
            UserId, [OpenChannelA, OpenChannelA, OpenChannelA], Permissions.ViewChannel);

        Assert.That(allowed, Is.EquivalentTo(new[] { OpenChannelA }));
    }

    // ── The bus contract ──────────────────────────────────────────────────────

    [Test]
    public async Task Handler_EchoesTheUserAndReturnsOnlyWhatIsPermitted()
    {
        await SeedAsync();
        await DenyAsync(PrivateChannelA, GuildA, Permissions.ViewChannel);

        var response = await HasUserPermissionsHandler.Handle(
            new FilterChannelsWithUserPermissionRequest
            {
                UserId = UserId,
                ChannelIds = [OpenChannelA, PrivateChannelA, OpenChannelB],
                Permission = ExternalPermission.ViewChannel,
            },
            _service);

        Assert.Multiple(() =>
        {
            Assert.That(response.UserId, Is.EqualTo(UserId));
            Assert.That(response.AllowedChannelIds, Is.EquivalentTo(new[] { OpenChannelA, OpenChannelB }));
        });
    }
}
