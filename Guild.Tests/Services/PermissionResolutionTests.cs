using Guild.Application.Bus.Consumers;
using Guild.Application.Bus.Events.Permission;
using Guild.Application.Services;
using Guild.Contracts.Bus.Commands;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Events.Permission;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// How a permission mask is actually resolved: implication, deny closure, overwrite ordering, and
/// the implicit @everyone role.
/// </summary>
[TestFixtureSource(typeof(GuildContextProviders))]
public class PermissionResolutionTests(IGuildContextProvider provider)
{
    private const string GuildId = "guld-1";
    private const string OwnerId = "user-owner";
    private const string UserId = "user-1";
    private const string MemberId = "memb-1";
    private const string ChannelId = "chan-1";
    private const string CategoryId = "catg-1";
    private const string EveryoneRoleId = "role-everyone";

    private MicroserviceContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _service = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = await provider.CreateAsync();
        _cache = new FakeDistributedCache();
        _service = PermissionTestFactory.Create(_cache, _context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ══════════════════════════════════════════════════════════════════════════ Seeding
    // ══════════════════════════════════════════════════════════════════════════

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private void AddGuild(string id = GuildId, string ownerId = OwnerId) =>
        _context.Guilds.Add(new global::Guild.Domain.Aggregates.Guild
        {
            Id = id, Name = "Test Guild", OwnerId = ownerId, CreatedAt = Now, UpdatedAt = Now,
        });

    private void AddCategory(string id = CategoryId) =>
        _context.Categories.Add(new Category
        {
            Id = id, GuildId = GuildId, Name = "Test Category", CreatedAt = Now, UpdatedAt = Now,
        });

    private void AddChannel(string id = ChannelId, string? categoryId = null) =>
        _context.Channels.Add(new Channel
        {
            Id = id, GuildId = GuildId, CategoryId = categoryId, Name = "test-channel",
            Description = "desc", Type = ChannelType.Text, CreatedAt = Now, UpdatedAt = Now,
        });

    private void AddRole(string id, Permissions permissions, RoleType type = RoleType.None, int position = 0) =>
        _context.Roles.Add(new Role
        {
            Id = id, GuildId = GuildId, Name = id, Type = type, Position = position,
            Permissions = permissions, CreatedAt = Now, UpdatedAt = Now,
        });

    /// <summary>The @everyone role exactly as a real guild has it: every bit a plain member is
    /// handed out of the box, which is the set that made R1 invisible.</summary>
    private void AddEveryoneRole(Permissions? permissions = null) =>
        AddRole(EveryoneRoleId, permissions ?? Role.DefaultEveryonePermissions, RoleType.Everyone);

    private void AddMember(string id = MemberId, string userId = UserId) =>
        _context.GuildMembers.Add(new GuildMember
        {
            Id = id, GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
            SearchValue = userId, CreatedAt = Now, UpdatedAt = Now,
        });

    private void AddRoleMember(string id, string roleId, string memberId = MemberId) =>
        _context.RoleMembers.Add(new RoleMember
        {
            Id = id, RoleId = roleId, MemberId = memberId, CreatedAt = Now, UpdatedAt = Now,
        });

    private void AddOverwrite(
        string id,
        string? channelId = null,
        string? categoryId = null,
        string? roleId = null,
        string? memberId = null,
        Permissions allow = Permissions.None,
        Permissions deny = Permissions.None) =>
        _context.Set<ChannelPermission>().Add(new ChannelPermission
        {
            Id = id, ChannelId = channelId, CategoryId = categoryId, RoleId = roleId, MemberId = memberId,
            AllowPermissions = allow, DenyPermissions = deny, CreatedAt = Now, UpdatedAt = Now,
        });

    /// <summary>A guild whose only role is @everyone, one channel, and one member who holds no
    /// RoleMember row at all - which after R12 is the ordinary case rather than a broken one.</summary>
    private async Task SeedDefaultGuildAsync(bool withCategory = false, bool withEveryoneRoleMemberRow = false)
    {
        AddGuild();
        AddEveryoneRole();
        AddMember();
        if (withCategory) AddCategory();
        AddChannel(categoryId: withCategory ? CategoryId : null);
        if (withEveryoneRoleMemberRow) AddRoleMember("rmem-everyone", EveryoneRoleId);
        await _context.SaveChangesAsync();
    }

    private async Task<Permissions> ResolveAsync(string channelId = ChannelId, string userId = UserId)
    {
        var result = await _service.ComputePermissionsForUserAsync(userId, GuildId);
        return result.Permissions.Single(p => p.ChannelId == channelId).Permissions;
    }

    // ══════════════════════════════════════════════════════════════════════════ R1 - a deny beats
    // an implication ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task EveryoneChannelDeny_ViewChannel_HidesTheChannel()
    {
        // The private-channel case.
        await SeedDefaultGuildAsync();
        AddOverwrite("chpr-1", channelId: ChannelId, roleId: EveryoneRoleId, deny: Permissions.ViewChannel);
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.False, "the channel must be hidden");
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.False, "nothing that implies ViewChannel may survive");
            Assert.That(perms.HasFlag(Permissions.Connect), Is.False);
            Assert.That(perms.HasFlag(Permissions.EditOwnMessages), Is.False);
            Assert.That(perms.HasFlag(Permissions.DeleteOwnMessages), Is.False);
            Assert.That(perms.HasFlag(Permissions.ManageOwnThreads), Is.False);
            Assert.That(perms.HasFlag(Permissions.SendMessagesInThreads), Is.False);
            Assert.That(perms.HasFlag(Permissions.AddReactions), Is.False);
            Assert.That(perms.HasFlag(Permissions.Speak), Is.False);
            Assert.That(perms.HasFlag(Permissions.Stream), Is.False);
        });
    }

    [Test]
    public async Task EveryoneChannelDeny_SendMessages_MakesTheChannelReadOnly()
    {
        // The announcement-channel case: the member keeps the channel, loses every way of writing
        // in it.
        await SeedDefaultGuildAsync();
        AddOverwrite("chpr-1", channelId: ChannelId, roleId: EveryoneRoleId, deny: Permissions.SendMessages);
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True, "the channel stays visible");
            Assert.That(perms.HasFlag(Permissions.Connect), Is.True, "voice is untouched by a text deny");
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.False);
            Assert.That(perms.HasFlag(Permissions.AddReactions), Is.False);
            Assert.That(perms.HasFlag(Permissions.AttachFiles), Is.False);
            Assert.That(perms.HasFlag(Permissions.EmbedLinks), Is.False);
            Assert.That(perms.HasFlag(Permissions.CreateThreads), Is.False);
        });
    }

    [Test]
    public async Task EveryoneCategoryDeny_ViewChannel_HidesEveryChannelInTheCategory()
    {
        await SeedDefaultGuildAsync(withCategory: true);
        AddChannel("chan-outside");
        AddOverwrite("chpr-1", categoryId: CategoryId, roleId: EveryoneRoleId, deny: Permissions.ViewChannel);
        await _context.SaveChangesAsync();

        var inside = await ResolveAsync();
        var outside = await ResolveAsync("chan-outside");

        Assert.Multiple(() =>
        {
            Assert.That(inside.HasFlag(Permissions.ViewChannel), Is.False, "inside the private category");
            Assert.That(inside.HasFlag(Permissions.SendMessages), Is.False);
            Assert.That(outside.HasFlag(Permissions.ViewChannel), Is.True, "a channel outside it is unaffected");
        });
    }

    [Test]
    public async Task EveryoneCategoryDeny_SendMessages_MakesTheCategoryReadOnly()
    {
        await SeedDefaultGuildAsync(withCategory: true);
        AddOverwrite("chpr-1", categoryId: CategoryId, roleId: EveryoneRoleId, deny: Permissions.SendMessages);
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.False);
            Assert.That(perms.HasFlag(Permissions.AddReactions), Is.False);
        });
    }

    [Test]
    public async Task ChannelAllow_GrantsExactlyTheBitsNamed_AndImpliesNothing()
    {
        // The documented allow-side decision, and the reason it is the right one: re-opening one
        // channel inside a private category has to be able to say "you may see this, and only see
        // it".
        await SeedDefaultGuildAsync(withCategory: true);
        AddOverwrite("chpr-cat", categoryId: CategoryId, roleId: EveryoneRoleId, deny: Permissions.ViewChannel);
        AddOverwrite("chpr-chan", channelId: ChannelId, roleId: EveryoneRoleId, allow: Permissions.ViewChannel);
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True, "re-opened by the channel allow");
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.False, "an allow grants only what it names");
            Assert.That(perms.HasFlag(Permissions.Connect), Is.False);
        });
    }

    [Test]
    public async Task ChannelAllow_OfAnImplyingBit_DoesNotDragTheImpliedBitBackIn()
    {
        // The inverse of the case above: allowing AddReactions after SendMessages was denied gives
        // exactly reactions, not the ability to post.
        await SeedDefaultGuildAsync();
        AddOverwrite("chpr-1", channelId: ChannelId, roleId: EveryoneRoleId,
            deny: Permissions.SendMessages, allow: Permissions.AddReactions);
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.AddReactions), Is.True);
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.False);
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True, "never denied, so still held");
        });
    }

    [Test]
    public async Task Deny_OfAnUnimplicatedBit_TouchesNothingElse()
    {
        // Negative case for the closure: a bit nothing implies must take nothing with it.
        await SeedDefaultGuildAsync();
        AddOverwrite("chpr-1", channelId: ChannelId, roleId: EveryoneRoleId, deny: Permissions.CreateInvite);
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.CreateInvite), Is.False);
            Assert.That(perms, Is.EqualTo(Role.DefaultEveryonePermissions & ~Permissions.CreateInvite),
                "the default mask is already closed under implication, so nothing else may move");
        });
    }

    [Test]
    public async Task NoOverwriteAtAll_LeavesTheFullDefaultMask()
    {
        await SeedDefaultGuildAsync();

        var perms = await ResolveAsync();

        Assert.That(perms, Is.EqualTo(Role.DefaultEveryonePermissions),
            "the default @everyone mask is closed under the implication table already");
    }

    [Test]
    public async Task Superadmin_BypassesAnEveryoneDeny()
    {
        // Discord's Administrator ignores overwrites.
        await SeedDefaultGuildAsync();
        AddRole("role-admin", Permissions.Superadmin, position: 5);
        AddRoleMember("rmem-admin", "role-admin");
        AddOverwrite("chpr-1", channelId: ChannelId, roleId: EveryoneRoleId, deny: Permissions.ViewChannel);
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // R9 - overwrite resolution is order-independent
    // ══════════════════════════════════════════════════════════════════════════

    [TestCase(true, TestName = "ConflictingRoleOverwrites_AllowWins_DenyInsertedFirst")]
    [TestCase(false, TestName = "ConflictingRoleOverwrites_AllowWins_AllowInsertedFirst")]
    public async Task ConflictingRoleOverwrites_AllowWinsRegardlessOfInsertionOrder(bool denyFirst)
    {
        // Two held roles disagreeing about one bit used to resolve to whichever row the database
        // handed back last.
        await SeedDefaultGuildAsync();
        AddRole("role-deny", Permissions.None, position: 1);
        AddRole("role-allow", Permissions.None, position: 2);
        AddRoleMember("rmem-deny", "role-deny");
        AddRoleMember("rmem-allow", "role-allow");

        if (denyFirst)
        {
            AddOverwrite("chpr-a", channelId: ChannelId, roleId: "role-deny", deny: Permissions.SendMessages);
            AddOverwrite("chpr-b", channelId: ChannelId, roleId: "role-allow", allow: Permissions.SendMessages);
        }
        else
        {
            AddOverwrite("chpr-a", channelId: ChannelId, roleId: "role-allow", allow: Permissions.SendMessages);
            AddOverwrite("chpr-b", channelId: ChannelId, roleId: "role-deny", deny: Permissions.SendMessages);
        }

        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True,
            "an allow on any held role beats a deny on another, whatever order the rows arrive in");
    }

    [Test]
    public async Task MemberOverwrite_BeatsBothRoleTiers()
    {
        // The member tier is applied last, so its deny survives an allow from a role overwrite -
        // the opposite outcome to the two-role case above, and the reason the tiers are ordered
        // rather than merged.
        await SeedDefaultGuildAsync();
        AddRole("role-allow", Permissions.None, position: 1);
        AddRoleMember("rmem-allow", "role-allow");
        AddOverwrite("chpr-role", channelId: ChannelId, roleId: "role-allow", allow: Permissions.SendMessages);
        AddOverwrite("chpr-member", channelId: ChannelId, memberId: MemberId, deny: Permissions.SendMessages);
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.That(perms.HasFlag(Permissions.SendMessages), Is.False);
    }

    [Test]
    public async Task HeldRoleDeny_BeatsAnEveryoneAllow()
    {
        // Tier order in the other direction: @everyone is resolved before held roles, so a role a
        // member actually holds can take away what the channel gives everybody.
        await SeedDefaultGuildAsync(withEveryoneRoleMemberRow: true);
        AddRole("role-muted", Permissions.None, position: 1);
        AddRoleMember("rmem-muted", "role-muted");
        AddOverwrite("chpr-everyone", channelId: ChannelId, roleId: EveryoneRoleId, allow: Permissions.SendMessages);
        AddOverwrite("chpr-muted", channelId: ChannelId, roleId: "role-muted", deny: Permissions.SendMessages);
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.That(perms.HasFlag(Permissions.SendMessages), Is.False);
    }

    [Test]
    public async Task OverwriteForARoleTheMemberDoesNotHold_IsIgnored()
    {
        await SeedDefaultGuildAsync();
        AddRole("role-other", Permissions.None, position: 1);
        AddOverwrite("chpr-other", channelId: ChannelId, roleId: "role-other", deny: Permissions.SendMessages);
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // R10 - Speak and Stream are independent bits
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Speak_DoesNotImplyStream()
    {
        await SeedDefaultGuildAsync();
        AddRole("role-voice", Permissions.Speak, position: 1);
        AddRoleMember("rmem-voice", "role-voice");
        AddOverwrite("chpr-1", channelId: ChannelId, roleId: EveryoneRoleId, deny: Permissions.Stream);
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.Speak), Is.True, "a speaker-only channel is expressible");
            Assert.That(perms.HasFlag(Permissions.Stream), Is.False);
        });
    }

    [Test]
    public async Task Stream_DoesNotImplySpeak()
    {
        // The presenter case: screen share without a microphone.
        await SeedDefaultGuildAsync();
        AddOverwrite("chpr-1", channelId: ChannelId, roleId: EveryoneRoleId, deny: Permissions.Speak);
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.Stream), Is.True);
            Assert.That(perms.HasFlag(Permissions.Speak), Is.False);
            Assert.That(perms.HasFlag(Permissions.Connect), Is.True, "denying Speak is not denying entry");
        });
    }

    [Test]
    public async Task SpeakAndStream_StillImplyConnect()
    {
        AddGuild();
        AddRole("role-voice", Permissions.Speak);
        AddMember();
        AddRoleMember("rmem-voice", "role-voice");
        AddChannel();
        await _context.SaveChangesAsync();

        var speakerPerms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(speakerPerms.HasFlag(Permissions.Connect), Is.True, "Speak implies Connect");
            Assert.That(speakerPerms.HasFlag(Permissions.ViewChannel), Is.True, "and Connect implies ViewChannel");
            Assert.That(speakerPerms.HasFlag(Permissions.Stream), Is.False, "but not Stream");
        });
    }

    [Test]
    public async Task DenyingConnect_StripsSpeakAndStreamAndTheVoiceModerationBits()
    {
        // The reverse edge that the forward Speak -> Connect implication buys: a member who cannot
        // enter the room cannot be left holding permissions that only mean anything inside it.
        await SeedDefaultGuildAsync();
        AddRole("role-mod", Permissions.MuteMembers | Permissions.DeafenMembers | Permissions.MoveMembers, position: 1);
        AddRoleMember("rmem-mod", "role-mod");
        AddOverwrite("chpr-1", channelId: ChannelId, roleId: EveryoneRoleId, deny: Permissions.Connect);
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.Connect), Is.False);
            Assert.That(perms.HasFlag(Permissions.Speak), Is.False);
            Assert.That(perms.HasFlag(Permissions.Stream), Is.False);
            Assert.That(perms.HasFlag(Permissions.MuteMembers), Is.False);
            Assert.That(perms.HasFlag(Permissions.DeafenMembers), Is.False);
            Assert.That(perms.HasFlag(Permissions.MoveMembers), Is.False);
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True, "denying voice is not denying the channel");
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // R11 - ManageChannel is not ManagePermissions
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ManageChannel_DoesNotImplyManagePermissions()
    {
        AddGuild();
        AddRole("role-manager", Permissions.ManageChannel);
        AddMember();
        AddRoleMember("rmem-manager", "role-manager");
        AddChannel();
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ManageChannel), Is.True);
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True, "managing a channel still implies seeing it");
            Assert.That(perms.HasFlag(Permissions.ManagePermissions), Is.False,
                "renaming a channel is not the right to rewrite who may enter it");
        });
    }

    [Test]
    public async Task DenyingViewChannel_StripsManageChannelAndManagePermissions()
    {
        await SeedDefaultGuildAsync();
        AddRole("role-manager", Permissions.ManageChannel | Permissions.ManagePermissions, position: 1);
        AddRoleMember("rmem-manager", "role-manager");
        AddOverwrite("chpr-1", channelId: ChannelId, roleId: EveryoneRoleId, deny: Permissions.ViewChannel);
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ManageChannel), Is.False);
            Assert.That(perms.HasFlag(Permissions.ManagePermissions), Is.False);
        });
    }

    [Test]
    public async Task DenyingManagePermissions_LeavesManageChannel()
    {
        // The implication runs one way only: ManagePermissions is downstream of ManageChannel in
        // neither direction now, so taking it away is a narrow act.
        AddGuild();
        AddRole("role-manager", Permissions.ManageChannel | Permissions.ManagePermissions);
        AddMember();
        AddRoleMember("rmem-manager", "role-manager");
        AddChannel();
        AddOverwrite("chpr-1", channelId: ChannelId, roleId: "role-manager", deny: Permissions.ManagePermissions);
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ManagePermissions), Is.False);
            Assert.That(perms.HasFlag(Permissions.ManageChannel), Is.True);
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ R12 - @everyone is
    // implicit ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MemberWithNoRoleMemberRows_StillResolvesEveryonePermissions()
    {
        await SeedDefaultGuildAsync();

        var perms = await ResolveAsync();

        Assert.That(perms, Is.EqualTo(Role.DefaultEveryonePermissions));
    }

    [Test]
    public async Task RedundantEveryoneRoleMemberRow_ResolvesIdentically()
    {
        // Existing rows are left in place rather than migrated away, so the union has to be
        // idempotent: holding the row and not holding it must produce the same number.
        await SeedDefaultGuildAsync(withEveryoneRoleMemberRow: true);

        var perms = await ResolveAsync();

        Assert.That(perms, Is.EqualTo(Role.DefaultEveryonePermissions));
    }

    [Test]
    public async Task NonMember_StillResolvesToNothing()
    {
        // The fail-closed branch is load-bearing and must survive making @everyone implicit: a
        // stranger, or someone just kicked or banned (both of which delete the GuildMember row),
        // must not pick up the guild's defaults just by asking.
        await SeedDefaultGuildAsync();

        var result = await _service.ComputePermissionsForUserAsync("stranger", GuildId);

        Assert.Multiple(() =>
        {
            Assert.That(result.BasePermissions, Is.EqualTo(Permissions.None));
            Assert.That(result.Permissions, Is.Empty);
        });
    }

    [Test]
    public async Task EveryoneRoleOfAnotherGuild_ContributesNothing()
    {
        // The implicit union is guild-scoped for the same reason the held-role query is: another
        // guild's @everyone role must not be able to reach across.
        await SeedDefaultGuildAsync();
        _context.Guilds.Add(new global::Guild.Domain.Aggregates.Guild
        {
            Id = "guld-other", Name = "Other", OwnerId = "user-other", CreatedAt = Now, UpdatedAt = Now,
        });
        _context.Roles.Add(new Role
        {
            Id = "role-everyone-other", GuildId = "guld-other", Name = "Everyone", Type = RoleType.Everyone,
            Permissions = Permissions.Superadmin, CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync();

        Assert.That(perms.HasFlag(Permissions.Superadmin), Is.False);
    }

    [Test]
    public async Task InstalledBot_InheritsEveryonePermissions()
    {
        // A bot install creates a GuildMember and nothing else, so before R12 an installed bot held
        // only the AllowPermissions it was granted and could not even see the channels it was
        // installed to serve.
        AddGuild();
        AddEveryoneRole();
        AddChannel();
        await _context.SaveChangesAsync();

        await CreateBotGuildMemberHandler.Handle(
            new CreateBotGuildMemberCommand
            {
                GuildId = GuildId,
                BotUserId = "user-bot",
                BotDisplayName = "Test Bot",
                InstalledByUserId = OwnerId,
                GrantedPermissions = (ulong)Permissions.ManageEvents,
            },
            _context,
            new AuditLogService(_context),
            new FakeHubContext(),
            new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance));
        await _context.SaveChangesAsync();

        var perms = await ResolveAsync(userId: "user-bot");

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True, "the bot can see the guild's channels");
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True);
            Assert.That(perms.HasFlag(Permissions.ManageEvents), Is.True, "and keeps what it was granted on install");
        });
    }

    [Test]
    public async Task GetHighestRolePosition_MemberWithoutEveryoneRow_RanksAtEveryonePosition()
    {
        AddGuild();
        AddMember();
        AddRole(EveryoneRoleId, Role.DefaultEveryonePermissions, RoleType.Everyone, position: 3);
        await _context.SaveChangesAsync();

        var position = await _service.GetHighestRolePositionAsync(UserId, GuildId);

        Assert.That(position, Is.EqualTo(3));
    }

    [Test]
    public async Task GetHighestRolePosition_TakesTheMaxOverHeldRolesAndEveryone()
    {
        AddGuild();
        AddMember();
        AddEveryoneRole();
        AddRole("role-staff", Permissions.None, position: 7);
        AddRoleMember("rmem-staff", "role-staff");
        await _context.SaveChangesAsync();

        var position = await _service.GetHighestRolePositionAsync(UserId, GuildId);

        Assert.That(position, Is.EqualTo(7));
    }

    [Test]
    public async Task GetHighestRolePosition_NonMember_IsStillMinValue()
    {
        AddGuild();
        AddEveryoneRole();
        await _context.SaveChangesAsync();

        var position = await _service.GetHighestRolePositionAsync("stranger", GuildId);

        Assert.That(position, Is.EqualTo(int.MinValue),
            "somebody who is not in the guild is not in @everyone either");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // R13 - invalidating an @everyone overwrite change
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task EveryoneOverwriteChange_InvalidatesAMemberWithNoRoleMemberRow()
    {
        await SeedDefaultGuildAsync();
        await _service.ComputePermissionsForUserAsync(UserId, GuildId);
        var key = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        Assume.That(_cache.HasEntry(key), Is.True);

        await ChannelPermissionChangedHandler.Handle(
            new ChannelPermissionChanged { GuildId = GuildId, RoleId = EveryoneRoleId }, _context, _service);

        Assert.That(_cache.HasEntry(key), Is.False,
            "an @everyone overwrite applies to every member, row or no row");
    }

    [Test]
    public async Task EveryoneOverwriteChange_InvalidatesEveryMemberOfTheGuild()
    {
        await SeedDefaultGuildAsync();
        AddMember("memb-2", "user-2");
        await _context.SaveChangesAsync();

        await _service.ComputePermissionsForUserAsync(UserId, GuildId);
        await _service.ComputePermissionsForUserAsync("user-2", GuildId);

        await ChannelPermissionChangedHandler.Handle(
            new ChannelPermissionChanged { GuildId = GuildId, RoleId = EveryoneRoleId }, _context, _service);

        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry(GuildPermissionsForUser.GetCacheKey(GuildId, UserId)), Is.False);
            Assert.That(_cache.HasEntry(GuildPermissionsForUser.GetCacheKey(GuildId, "user-2")), Is.False);
        });
    }

    [Test]
    public async Task OrdinaryRoleOverwriteChange_OnlyInvalidatesTheRolesHolders()
    {
        // The negative half: the whole-guild sweep must stay reserved for @everyone, or every
        // overwrite edit on a two-member role would evict the whole guild's cache.
        await SeedDefaultGuildAsync();
        AddMember("memb-2", "user-2");
        AddRole("role-staff", Permissions.None, position: 1);
        AddRoleMember("rmem-staff", "role-staff");
        await _context.SaveChangesAsync();

        await _service.ComputePermissionsForUserAsync(UserId, GuildId);
        await _service.ComputePermissionsForUserAsync("user-2", GuildId);

        await ChannelPermissionChangedHandler.Handle(
            new ChannelPermissionChanged { GuildId = GuildId, RoleId = "role-staff" }, _context, _service);

        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry(GuildPermissionsForUser.GetCacheKey(GuildId, UserId)), Is.False,
                "the holder is invalidated");
            Assert.That(_cache.HasEntry(GuildPermissionsForUser.GetCacheKey(GuildId, "user-2")), Is.True,
                "a member who does not hold the role keeps their entry");
        });
    }

    [Test]
    public async Task EveryoneRoleOfAnotherGuild_DoesNotTriggerTheWholeGuildSweep()
    {
        // The role-type lookup is guild-scoped, so an event naming a foreign @everyone role falls
        // through to the ordinary membership walk rather than evicting this guild wholesale.
        await SeedDefaultGuildAsync();
        _context.Guilds.Add(new global::Guild.Domain.Aggregates.Guild
        {
            Id = "guld-other", Name = "Other", OwnerId = "user-other", CreatedAt = Now, UpdatedAt = Now,
        });
        _context.Roles.Add(new Role
        {
            Id = "role-everyone-other", GuildId = "guld-other", Name = "Everyone", Type = RoleType.Everyone,
            CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        await _service.ComputePermissionsForUserAsync(UserId, GuildId);

        await ChannelPermissionChangedHandler.Handle(
            new ChannelPermissionChanged { GuildId = GuildId, RoleId = "role-everyone-other" }, _context, _service);

        Assert.That(_cache.HasEntry(GuildPermissionsForUser.GetCacheKey(GuildId, UserId)), Is.True);
    }

    [Test]
    public async Task MemberTargetedChange_InvalidatesOnlyThatMember()
    {
        await SeedDefaultGuildAsync();
        AddMember("memb-2", "user-2");
        await _context.SaveChangesAsync();

        await _service.ComputePermissionsForUserAsync(UserId, GuildId);
        await _service.ComputePermissionsForUserAsync("user-2", GuildId);

        await ChannelPermissionChangedHandler.Handle(
            new ChannelPermissionChanged { GuildId = GuildId, MemberId = MemberId }, _context, _service);

        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry(GuildPermissionsForUser.GetCacheKey(GuildId, UserId)), Is.False);
            Assert.That(_cache.HasEntry(GuildPermissionsForUser.GetCacheKey(GuildId, "user-2")), Is.True);
        });
    }
}
