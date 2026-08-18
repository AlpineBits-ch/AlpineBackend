using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Messaging.Contracts.Bus.Request;
using Messaging.Contracts.Bus.Response;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// What the Mentions tab may show - it is subject to <see cref="Permissions.ReadMessageHistory"/>,
/// not only to <see cref="Permissions.ViewChannel"/> - and what it may name.
/// </summary>
[TestFixtureSource(typeof(GuildContextProviders))]
public class InboxMentionHistoryTests(IGuildContextProvider provider)
{
    private const string GuildId = "guld-1";
    private const string OwnerId = "user-owner";
    private const string UserId = "user-1";
    private const string MemberId = "memb-1";
    private const string ChannelId = "chan-1";
    private const string EveryoneRoleId = "role-everyone";
    private const string PingedRoleId = "role-team";
    private const string MessageId = "mesg-1";
    private const string PersonaMessageId = "mesg-2";

    private MicroserviceContext _context = null!;
    private GuildPermissionService _permissions = null!;
    private FakeInvokingMessageBus _bus = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = await provider.CreateAsync();
        _permissions = new GuildPermissionService(
            new FakeDistributedCache(), _context, NullLogger<GuildPermissionService>.Instance);

        _bus = new FakeInvokingMessageBus();
        _bus.SetResponse<GetMessageRequest>(new GetMessageResponse
        {
            Message = new MessageSummary
            {
                Id = MessageId, CreatedAt = SentAt, AuthorId = OwnerId, ChannelId = ChannelId,
                Content = "heads up"u8.ToArray(),
            },
        });
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;
    private static readonly DateTimeOffset JoinedAt = DateTimeOffset.UtcNow.AddDays(-3);
    private static readonly DateTimeOffset SentAt = DateTimeOffset.UtcNow.AddHours(-1);

    /// <summary>A guild where the caller holds a role, and one message that pinged it.</summary>
    private async Task SeedAsync(Permissions? deniedOnChannel = null)
    {
        _context.Guilds.Add(new global::Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, Name = "Test Guild", OwnerId = OwnerId, CreatedAt = Now, UpdatedAt = Now,
        });

        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Name = Role.EveryoneRoleName,
            Type = RoleType.Everyone, Position = 0, Permissions = Role.DefaultEveryonePermissions,
            CreatedAt = Now, UpdatedAt = Now,
        });

        _context.Roles.Add(new Role
        {
            Id = PingedRoleId, GuildId = GuildId, Name = "Team", Position = 1,
            Permissions = Permissions.None, CreatedAt = JoinedAt, UpdatedAt = JoinedAt,
        });

        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "general", Description = "d",
            Type = ChannelType.Text, CreatedAt = Now, UpdatedAt = Now,
        });

        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = JoinedAt.UtcDateTime,
            SearchValue = UserId, CreatedAt = JoinedAt, UpdatedAt = JoinedAt,
        });

        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rmem-1", RoleId = PingedRoleId, MemberId = MemberId,
            CreatedAt = JoinedAt, UpdatedAt = JoinedAt,
        });

        _context.ChannelBroadcastMentions.Add(new ChannelBroadcastMention
        {
            Id = "bcme-1", ChannelId = ChannelId, MessageId = MessageId, MessageCreatedAt = SentAt,
            AuthorId = OwnerId, Kind = BroadcastMentionKind.Role, RoleId = PingedRoleId,
            CreatedAt = SentAt, UpdatedAt = SentAt,
        });

        if (deniedOnChannel is { } denied)
        {
            _context.Set<ChannelPermission>().Add(new ChannelPermission
            {
                Id = "chpr-1", ChannelId = ChannelId, RoleId = EveryoneRoleId,
                DenyPermissions = denied, CreatedAt = Now, UpdatedAt = Now,
            });
        }

        await _context.SaveChangesAsync();
    }

    /// <summary>A character adopted into the guild, and one indexed mention of it.</summary>
    private async Task SeedPersonaMentionAsync(string personaId, string ownerUserId)
    {
        _context.Set<Persona>().Add(new Persona
        {
            Id = personaId, Scope = PersonaScope.User, OwnerUserId = ownerUserId, Name = "Mayor Cogsgrove",
            CreatedAt = Now, UpdatedAt = Now,
        });

        _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
        {
            Id = $"pgpf-{personaId}", PersonaId = personaId, GuildId = GuildId,
            ApprovalState = PersonaApprovalState.Approved, CreatedAt = Now, UpdatedAt = Now,
        });

        await _context.SaveChangesAsync();

        _bus.SetResponse<GetUserMentionsRequest>(new GetUserMentionsResponse
        {
            Mentions =
            [
                new UserMentionEntry
                {
                    UserId = UserId, CreatedAt = SentAt, MessageId = PersonaMessageId,
                    ContextId = ChannelId, GuildId = GuildId, ChannelId = ChannelId,
                    AuthorId = OwnerId, Kind = PersonaMentionService.MentionKindName,
                },
            ],
        });

        _bus.SetResponse<GetMessageRequest>(new GetMessageResponse
        {
            Message = new MessageSummary
            {
                Id = PersonaMessageId, CreatedAt = SentAt, AuthorId = OwnerId, ChannelId = ChannelId,
                Content = System.Text.Encoding.UTF8.GetBytes($"<@{personaId}> your move"),
            },
        });
    }

    private async Task<InboxMentionDto> PersonaMentionAsync()
    {
        var page = await MentionsAsync();
        return page.Mentions.Single(m => m.MessageId == PersonaMessageId);
    }

    private async Task<InboxMentionPageDto> MentionsAsync()
    {
        var service = new InboxMentionService(
            _context, new NotificationResolutionService(_context), _permissions,
            new PersonaMentionService(_context, new PersonaService(new FakeDistributedCache(), _context)),
            _bus, NullLogger<InboxMentionService>.Instance);

        return await service.GetMentionsAsync(UserId, new MentionFilter(), 25, null);
    }

    private async Task<int> MentionCountAsync()
    {
        var service = new InboxMentionService(
            _context, new NotificationResolutionService(_context), _permissions,
            new PersonaMentionService(_context, new PersonaService(new FakeDistributedCache(), _context)),
            _bus, NullLogger<InboxMentionService>.Instance);

        var page = await service.GetMentionsAsync(UserId, new MentionFilter(), 25, null);
        return page.Mentions.Count;
    }

    // ── Normal ────────────────────────────────────────────────────────────────

    [Test]
    public async Task DefaultEveryone_TheRolePingIsShown()
    {
        // The regression guard.
        await SeedAsync();

        Assert.That(await MentionCountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task TheBodyLookupNamesTheCaller()
    {
        // The per-message fetch used to carry no principal, so this tab's caller-side check was the
        // only thing standing between a mention row and a message body.
        await SeedAsync();

        await MentionCountAsync();

        var request = _bus.Invoked.OfType<GetMessageRequest>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(request.RequestingUserId, Is.EqualTo(UserId));
            Assert.That(request.Scope, Is.EqualTo(MessageReadScope.Full),
                "the tab renders the body, so it asks the full-scope question");
        });
    }

    // ── Negative ──────────────────────────────────────────────────────────────

    [Test]
    public async Task HistoryDenied_TheRolePingIsDropped()
    {
        await SeedAsync(deniedOnChannel: Permissions.ReadMessageHistory);

        Assert.That(await MentionCountAsync(), Is.Zero,
            "being pinged must not be a way around a channel that withholds its backlog");
    }

    [Test]
    public async Task ViewChannelDenied_TheRolePingIsDropped()
    {
        // The check that already existed, re-asserted because it now shares a memo with the history
        // check - an && that evaluated the wrong side first would lose it without any other test
        // noticing.
        await SeedAsync(deniedOnChannel: Permissions.ViewChannel);

        Assert.That(await MentionCountAsync(), Is.Zero);
    }

    // ── Persona mentions ──────────────────────────────────────────────────────

    [Test]
    public async Task APersonaMention_SaysWhichCharacterWasPinged()
    {
        await SeedAsync();
        await SeedPersonaMentionAsync("pers_mayor", ownerUserId: UserId);

        var mention = await PersonaMentionAsync();

        Assert.Multiple(() =>
        {
            Assert.That(mention.Kind, Is.EqualTo(PersonaMentionService.MentionKindName));
            Assert.That(mention.PersonaId, Is.EqualTo("pers_mayor"));
        });
    }

    [Test]
    public async Task ACharacterSomebodyElseAnswersFor_IsNotNamed()
    {
        await SeedAsync();
        await SeedPersonaMentionAsync("pers_mayor", ownerUserId: OwnerId);

        var mention = await PersonaMentionAsync();

        Assert.That(mention.PersonaId, Is.Null,
            "naming a character the caller does not answer for would say who is behind it");
    }
}
