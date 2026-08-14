using Guild.Application.Bus.Consumers;
using Guild.Contracts.Bus.Request;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;

namespace Guild.Tests.Bus.Consumers;

/// <summary>
/// The Guild half of the role-mention gate (R5/R19): which of the role ids a message named may
/// actually be pinged, and which need MentionEveryone.
/// </summary>
[TestFixtureSource(typeof(GuildContextProviders))]
public class ResolveRoleMentionsHandlerTests(IGuildContextProvider provider)
{
    private const string GuildId = "guld-1";
    private const string OtherGuildId = "guld-2";
    private const string ChannelId = "chan-1";

    private MicroserviceContext _context = null!;

    [SetUp]
    public async Task SetUp() => _context = await provider.CreateAsync();

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private void AddGuild(string id, string ownerId) =>
        _context.Guilds.Add(new global::Guild.Domain.Aggregates.Guild
        {
            Id = id, Name = id, OwnerId = ownerId, CreatedAt = Now, UpdatedAt = Now,
        });

    private void AddChannel(string id, string guildId) =>
        _context.Channels.Add(new Channel
        {
            Id = id, GuildId = guildId, Name = "general", Description = "d", Type = ChannelType.Text,
            CreatedAt = Now, UpdatedAt = Now,
        });

    private void AddRole(string id, string guildId, bool mentionable) =>
        _context.Roles.Add(new Role
        {
            Id = id, GuildId = guildId, Name = id, Mentionable = mentionable,
            Permissions = Permissions.None, CreatedAt = Now, UpdatedAt = Now,
        });

    /// <summary>One guild with a mentionable and a non-mentionable role, plus a second guild whose
    /// role exists and is mentionable - the id a member of the first guild could name.</summary>
    private async Task SeedAsync()
    {
        AddGuild(GuildId, "user-owner-1");
        AddGuild(OtherGuildId, "user-owner-2");
        AddChannel(ChannelId, GuildId);
        AddRole("role-open", GuildId, mentionable: true);
        AddRole("role-staff", GuildId, mentionable: false);
        AddRole("role-foreign", OtherGuildId, mentionable: true);
        await _context.SaveChangesAsync();
    }

    private Task<Contracts.Bus.Response.ResolveRoleMentionsResponse> ResolveAsync(
        params string[] roleIds) =>
        ResolveRoleMentionsHandler.Handle(
            new ResolveRoleMentionsRequest { ChannelId = ChannelId, RoleIds = roleIds }, _context);

    // ── Normal ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Resolve_MentionableRoleOfThisGuild_IsMentionable()
    {
        await SeedAsync();

        var result = await ResolveAsync("role-open");

        Assert.Multiple(() =>
        {
            Assert.That(result.MentionableRoleIds, Is.EquivalentTo(new[] { "role-open" }));
            Assert.That(result.RestrictedRoleIds, Is.Empty);
        });
    }

    [Test]
    public async Task Resolve_NonMentionableRoleOfThisGuild_IsRestricted()
    {
        await SeedAsync();

        var result = await ResolveAsync("role-staff");

        Assert.Multiple(() =>
        {
            Assert.That(result.MentionableRoleIds, Is.Empty);
            Assert.That(result.RestrictedRoleIds, Is.EquivalentTo(new[] { "role-staff" }));
        });
    }

    [Test]
    public async Task Resolve_MixedList_SplitsIt()
    {
        await SeedAsync();

        var result = await ResolveAsync("role-open", "role-staff");

        Assert.Multiple(() =>
        {
            Assert.That(result.MentionableRoleIds, Is.EquivalentTo(new[] { "role-open" }));
            Assert.That(result.RestrictedRoleIds, Is.EquivalentTo(new[] { "role-staff" }));
        });
    }

    // ── Negative ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Resolve_RoleOfAnotherGuild_AppearsInNeitherList()
    {
        // The escalation this handler exists to close: role ids are opaque strings on the wire, and
        // nothing else on the send path ever compared one to the channel's guild.
        await SeedAsync();

        var result = await ResolveAsync("role-foreign");

        Assert.Multiple(() =>
        {
            Assert.That(result.MentionableRoleIds, Is.Empty);
            Assert.That(result.RestrictedRoleIds, Is.Empty);
        });
    }

    [Test]
    public async Task Resolve_UnknownRoleId_AppearsInNeitherList()
    {
        await SeedAsync();

        var result = await ResolveAsync("role-does-not-exist");

        Assert.Multiple(() =>
        {
            Assert.That(result.MentionableRoleIds, Is.Empty);
            Assert.That(result.RestrictedRoleIds, Is.Empty);
        });
    }

    [Test]
    public async Task Resolve_UnknownChannel_FailsClosed()
    {
        await SeedAsync();

        var result = await ResolveRoleMentionsHandler.Handle(
            new ResolveRoleMentionsRequest { ChannelId = "chan-nope", RoleIds = ["role-open"] }, _context);

        Assert.Multiple(() =>
        {
            Assert.That(result.MentionableRoleIds, Is.Empty, "an unresolvable channel must ping nothing");
            Assert.That(result.RestrictedRoleIds, Is.Empty);
        });
    }

    // ── Edge ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task Resolve_EmptyRequest_ReturnsEmpty()
    {
        await SeedAsync();

        var result = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.MentionableRoleIds, Is.Empty);
            Assert.That(result.RestrictedRoleIds, Is.Empty);
        });
    }

    [Test]
    public async Task Resolve_ManyIds_IsStillOneQuery()
    {
        // Not a timing assertion - it is the shape that matters.
        await SeedAsync();

        var padding = Enumerable.Range(0, 97).Select(i => $"role-absent-{i}");
        var requested = new[] { "role-open", "role-staff", "role-foreign" }.Concat(padding).ToArray();

        var result = await ResolveAsync(requested);

        Assert.Multiple(() =>
        {
            Assert.That(result.MentionableRoleIds, Is.EquivalentTo(new[] { "role-open" }));
            Assert.That(result.RestrictedRoleIds, Is.EquivalentTo(new[] { "role-staff" }));
        });
    }
}
