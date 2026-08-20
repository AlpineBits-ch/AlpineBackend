using Guild.Application.Bus.Consumers;
using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Role = Guild.Domain.Aggregates.Role;

namespace Guild.Tests.Bus.Consumers;

/// <summary>Covers the role half of the bot's GUILD_CREATE hydration (R21).</summary>
[TestFixture]
public class GetGuildSnapshotForBotHandlerTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string BotUserId = "usr_bot1";

    private TestGuildContext _context = null!;
    private GuildPermissionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _service = PermissionTestFactory.Create(new FakeDistributedCache(), _context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>Seeds a guild with no channels on purpose: the channel list is filtered through the
    /// bot's own ViewChannel resolution, which is not what these assertions are about.</summary>
    private async Task SeedGuildAsync(params Role[] roles)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "g",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.AddRange(roles);
        await _context.SaveChangesAsync();
    }

    private static Role MakeRole(string id) => new()
    {
        Id = id, GuildId = GuildId, Name = "Staff", Color = "#123456", Position = 2,
        Type = RoleType.None, Permissions = Permissions.ManageChannel,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private Task<Guild.Contracts.Bus.Response.GetGuildSnapshotForBotResponse> HandleAsync() =>
        GetGuildSnapshotForBotHandler.Handle(
            new GetGuildSnapshotForBotRequest { GuildId = GuildId, BotUserId = BotUserId }, _context, _service);

    [Test]
    public async Task Handle_ProjectsHoistMentionableAndManaged()
    {
        var role = MakeRole("role-1");
        role.Hoist = true;
        role.Mentionable = false;
        role.IsManaged = true;
        role.BotUserId = BotUserId;
        await SeedGuildAsync(role);

        var snapshot = (await HandleAsync()).Guild!.Roles.Single();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Hoist, Is.True);
            Assert.That(snapshot.Mentionable, Is.False);
            Assert.That(snapshot.Managed, Is.True);
            Assert.That(snapshot.BotUserId, Is.EqualTo(BotUserId));
            Assert.That(snapshot.IntegrationId, Is.Null);
        });
    }

    [Test]
    public async Task Handle_ProjectsTheRoleBadge()
    {
        var role = MakeRole("role-1");
        role.SetBadge(iconUrl: null, unicodeEmoji: "\U0001F984");
        await SeedGuildAsync(role);

        var snapshot = (await HandleAsync()).Guild!.Roles.Single();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.UnicodeEmoji, Is.EqualTo("\U0001F984"));
            Assert.That(snapshot.IconUrl, Is.Null, "a role carries an icon or an emoji, never both");
        });
    }

    [Test]
    public async Task Handle_OrdinaryRole_HasNoOwnerIds()
    {
        await SeedGuildAsync(MakeRole("role-1"));

        var snapshot = (await HandleAsync()).Guild!.Roles.Single();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Managed, Is.False);
            Assert.That(snapshot.BotUserId, Is.Null);
            Assert.That(snapshot.IntegrationId, Is.Null);
            Assert.That(snapshot.Permissions, Is.EqualTo((ulong)Permissions.ManageChannel));
        });
    }

    [Test]
    public async Task Handle_UnknownGuild_ReturnsNullGuild()
    {
        var response = await GetGuildSnapshotForBotHandler.Handle(
            new GetGuildSnapshotForBotRequest { GuildId = "guild-missing", BotUserId = BotUserId },
            _context, _service);

        Assert.That(response.Guild, Is.Null);
    }
}
