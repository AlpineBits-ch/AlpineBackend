using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Services;
using Guild.Tests.Helpers;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using MessagingMessageType = Messaging.Contracts.Bus.Commands.MessageType;

namespace Guild.Tests.Endpoints;

/// <summary>
/// The roll route: what it refuses, and that a roll made in character keeps naming the account
/// that made it.
/// </summary>
[TestFixture]
public class DiceEndpointTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "chan-1";
    private const string UserId = "user-1";
    private const string MemberId = "member-1";
    private const string RoleId = "role-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissions = null!;
    private PersonaService _personas = null!;
    private FakeInvokingMessageBus _bus = null!;
    private DiceEndpoint _endpoint = null!;
    private Message _message = null!;

    /// <summary>Fixed faces, so an expectation can name the total.</summary>
    private sealed class QueueRoller(params int[] faces) : IDieRoller
    {
        private int _index;

        public int Roll(int sides) => _index < faces.Length ? faces[_index++] : 1;
    }

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _personas = new PersonaService(_cache, _context);
        _bus = new FakeInvokingMessageBus();
        _message = Message.Create(new CreateMessageParams
        {
            Content = [],
            ChannelId = ChannelId,
            AuthorId = UserId,
        });
        _bus.SetResponse<CreateMessageCommand>(_message);
        _endpoint = new DiceEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedGuildAsync(
        GuildFeatures features = GuildFeatures.Dice | GuildFeatures.Personas,
        ModulePermissions module = ModulePermissions.RollDice | ModulePermissions.UsePersonas,
        Permissions permissions = Permissions.ViewChannel | Permissions.SendMessages)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = "owner-1", Name = "Blackwater", Features = features,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Name = "Players",
            Permissions = permissions, ModulePermissions = module,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = RoleId, MemberId = MemberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "the-inn", Type = ChannelType.Text,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
    }

    private async Task SeedPersonaAsync(string id, string name)
    {
        _context.Set<Persona>().Add(new Persona
        {
            Id = id, Scope = PersonaScope.User, OwnerUserId = UserId, Name = name,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
        {
            Id = $"profile-{id}", PersonaId = id, GuildId = GuildId,
            ApprovalState = PersonaApprovalState.Approved,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
    }

    private Task<IResult> RollAsync(CreateDiceRollDto dto, params int[] faces) =>
        _endpoint.RollAsync(GuildId, ChannelId, dto, _permissions, _personas,
            new QueueRoller(faces), _context, _bus, TestPrincipal.Create(UserId));

    private CreateMessageCommand SentCommand() =>
        (CreateMessageCommand)_bus.Invoked.Single(m => m is CreateMessageCommand);

    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Roll_Public_PostsTheResultAndRecordsIt()
    {
        await SeedGuildAsync();

        var result = await RollAsync(new CreateDiceRollDto { Expression = "2d6+3", Reason = "Perception" }, 4, 5);
        await _context.SaveChangesAsync();

        var stored = _context.Set<DiceRoll>().Single();
        var command = SentCommand();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<DiceRollDto>>());
            Assert.That(((Ok<DiceRollDto>)result).Value!.Total, Is.EqualTo(12));
            Assert.That(stored.Total, Is.EqualTo(12));
            Assert.That(stored.MessageId, Is.EqualTo(_message.Id), "the record is keyed to the message Messaging returned");
            Assert.That(stored.Expression, Is.EqualTo("2d6 + 3"));
            Assert.That(command.Type, Is.EqualTo(MessagingMessageType.DiceRoll));
        });
    }

    [Test]
    public async Task Roll_Public_ContentIsReadableWithoutTheStructuredHalf()
    {
        await SeedGuildAsync();

        await RollAsync(new CreateDiceRollDto { Expression = "2d6+3", Reason = "Perception" }, 4, 5);

        var command = SentCommand();
        var content = System.Text.Encoding.UTF8.GetString(command.Content);

        Assert.Multiple(() =>
        {
            Assert.That(content, Is.EqualTo("Perception: 2d6 (4, 5) + 3 = 12"));
            Assert.That(command.EmbedsJson, Does.Contain("\"total\":12"),
                "the structured roll rides alongside the text, on the EmbedsJson precedent");
        });
    }

    [TestCase(DiceVisibility.GameMasterOnly)]
    [TestCase(DiceVisibility.Blind)]
    public async Task Roll_HiddenVisibility_IsRefusedRatherThanDowngraded(DiceVisibility visibility)
    {
        await SeedGuildAsync();

        var result = await RollAsync(new CreateDiceRollDto { Expression = "1d20", Visibility = visibility }, 11);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>());
            Assert.That(_bus.Invoked, Is.Empty, "a refused roll must not still be posted publicly");
            Assert.That(_context.Set<DiceRoll>().Count(), Is.Zero);
        });
    }

    [Test]
    public async Task Roll_InCharacter_CarriesThePersonaAndKeepsTheRealAuthor()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync("mayor", "Mayor Cogsgrove");

        await RollAsync(new CreateDiceRollDto { Expression = "1d20", PersonaId = "mayor" }, 17);
        await _context.SaveChangesAsync();

        var command = SentCommand();
        var stored = _context.Set<DiceRoll>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(command.PersonaId, Is.EqualTo("mayor"));
            Assert.That(command.AuthorDisplayName, Is.EqualTo("Mayor Cogsgrove"));
            Assert.That(command.AuthorIdType, Is.EqualTo(AuthorIdType.Persona));
            Assert.That(command.AuthorId, Is.EqualTo(UserId), "the costume never replaces the account");
            Assert.That(stored.RollerUserId, Is.EqualTo(UserId));
            Assert.That(stored.PersonaId, Is.EqualTo("mayor"));
        });
    }

    [Test]
    public async Task Roll_NoPersona_LeavesTheAuthorAlone()
    {
        await SeedGuildAsync();

        await RollAsync(new CreateDiceRollDto { Expression = "1d20" }, 9);

        var command = SentCommand();

        Assert.Multiple(() =>
        {
            Assert.That(command.PersonaId, Is.Null);
            Assert.That(command.AuthorIdType, Is.EqualTo(AuthorIdType.User));
            Assert.That(command.AuthorDisplayName, Is.Null);
        });
    }

    [Test]
    public async Task Roll_PersonaTheCallerCannotUse_IsForbidden()
    {
        await SeedGuildAsync();

        var result = await RollAsync(new CreateDiceRollDto { Expression = "1d20", PersonaId = "somebody-elses" }, 9);

        Assert.Multiple(() =>
        {
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status403Forbidden));
            Assert.That(_bus.Invoked, Is.Empty);
        });
    }

    [Test]
    public async Task Roll_ModuleDisabled_IsForbidden()
    {
        await SeedGuildAsync(features: GuildFeatures.Personas);

        var result = await RollAsync(new CreateDiceRollDto { Expression = "1d20" }, 9);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Roll_WithoutRollDice_IsForbidden()
    {
        await SeedGuildAsync(module: ModulePermissions.UsePersonas);

        var result = await RollAsync(new CreateDiceRollDto { Expression = "1d20" }, 9);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Roll_WithoutSendMessages_IsForbidden()
    {
        await SeedGuildAsync(permissions: Permissions.ViewChannel);

        var result = await RollAsync(new CreateDiceRollDto { Expression = "1d20" }, 9);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>(), "a roll is a post");
            Assert.That(_bus.Invoked, Is.Empty);
        });
    }

    [Test]
    public async Task Roll_MalformedExpression_IsRefusedBeforeAnythingIsPosted()
    {
        await SeedGuildAsync();

        var result = await RollAsync(new CreateDiceRollDto { Expression = "999999d999999" });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>());
            Assert.That(_bus.Invoked, Is.Empty);
        });
    }

    [Test]
    public async Task Roll_ChannelInAnotherGuild_IsNotFound()
    {
        await SeedGuildAsync();

        var result = await _endpoint.RollAsync(GuildId, "chan-elsewhere",
            new CreateDiceRollDto { Expression = "1d20" }, _permissions, _personas,
            new QueueRoller(9), _context, _bus, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    private static int StatusOf(IResult result) => result switch
    {
        IStatusCodeHttpResult status => status.StatusCode ?? 0,
        _ => 0,
    };
}
