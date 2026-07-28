using Guild.Application.Bus.Consumers;
using Guild.Application.Services;
using Guild.Contracts.Bus.Commands;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Wolverine;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;

namespace Guild.Tests.Bus.Consumers;

/// <summary>
/// FakeMessageBus (Guild.Tests/Helpers) only supports PublishAsync - ImportGuildStructureHandler
/// needs InvokeAsync&lt;GetProfileByUserIdResponse&gt; to resolve the owner's display name/search
/// value, so this test-local fake wires that one call up instead of adding NotImplementedException
/// noise to the shared helper for a case nothing else needs yet.
/// </summary>
internal class FakeProfileMessageBus : IMessageBus
{
    public ProfileDto? Profile { get; set; }

    public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        if (message is Social.Contracts.Bus.Integration.Request.GetProfileByUserIdRequest)
        {
            return Task.FromResult((T)(object)new GetProfileByUserIdResponse { Profile = Profile });
        }
        throw new NotImplementedException();
    }

    public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null) => ValueTask.CompletedTask;
    public Guid? CorrelationId => null;
    public string? TenantId { get; set; }
    public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null) => throw new NotImplementedException();
    public Task InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
    public Task InvokeAsync(object message, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
    public Task<T> InvokeAsync<T>(object message, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
    public Task InvokeForTenantAsync(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
    public Task<T> InvokeForTenantAsync<T>(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
    public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(object message, CancellationToken cancellation = default) => throw new NotImplementedException();
    public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(object message, DeliveryOptions options, CancellationToken cancellation = default) => throw new NotImplementedException();
    public Task<TResponse> StreamAsync<TRequest, TResponse>(IAsyncEnumerable<TRequest> messages, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
    public Task<TResponse> StreamAsync<TRequest, TResponse>(IAsyncEnumerable<TRequest> messages, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
    public ValueTask BroadcastToTopicAsync(string topicName, object message, DeliveryOptions? options = null) => throw new NotImplementedException();
    public IReadOnlyList<Envelope> PreviewSubscriptions(object message) => throw new NotImplementedException();
    public IReadOnlyList<Envelope> PreviewSubscriptions(object message, DeliveryOptions options) => throw new NotImplementedException();
    public IDestinationEndpoint EndpointFor(Uri uri) => throw new NotImplementedException();
    public IDestinationEndpoint EndpointFor(string endpointName) => throw new NotImplementedException();
}

[TestFixture]
public class ImportGuildStructureHandlerTests
{
    private const string OwnerId = "owner-1";

    private string _dbName = null!;
    private TestGuildContext _context = null!;
    private AuditLogService _auditLog = null!;
    private FakeProfileMessageBus _bus = null!;
    private FakeHubContext _hub = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _context = new TestGuildContext(_dbName);
        _auditLog = new AuditLogService(_context);
        _bus = new FakeProfileMessageBus { Profile = new ProfileDto { UserName = "owner", Hash = 1 } };
        _hub = new FakeHubContext();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ImportGuildStructureCommand BasicCommand() => new()
    {
        OwnerId = OwnerId,
        Name = "Imported Server",
        Description = "from discord",
        Roles =
        [
            new ImportedRoleDto { DiscordId = "discordguild1", Name = "@everyone", Color = "#000000", Position = 0, Permissions = 0b11, IsEveryoneRole = true },
            new ImportedRoleDto { DiscordId = "role-mod", Name = "Moderator", Color = "#FF0000", Position = 1, Permissions = 0b1000 },
        ],
        Categories =
        [
            new ImportedCategoryDto
            {
                DiscordId = "cat-1",
                Name = "General",
                Position = 0,
                Channels =
                [
                    new ImportedChannelDto
                    {
                        DiscordId = "chan-1",
                        Name = "general",
                        Type = "Text",
                        Position = 0,
                        Overwrites =
                        [
                            new ImportedOverwriteDto { DiscordRoleId = "role-mod", AllowPermissions = 0b1000, DenyPermissions = 0 },
                        ],
                    },
                ],
            },
        ],
    };

    [Test]
    public async Task Handle_DefaultChannelsAreSkipped_OnlyImportedCategoryExists()
    {
        var response = await Invoke(BasicCommand());

        var guild = _context.Guilds.Single(g => g.Id == response.GuildId);
        Assert.Multiple(() =>
        {
            Assert.That(guild.OwnerId, Is.EqualTo(OwnerId));
            Assert.That(_context.Categories.Count(c => c.GuildId == guild.Id), Is.EqualTo(1));
            Assert.That(_context.Categories.Single(c => c.GuildId == guild.Id).Name, Is.EqualTo("General"));
            Assert.That(guild.SystemChannelId, Is.Null,
                "SystemChannelId must stay unset to avoid the same circular-FK issue GuildEndpoint.CreateGuild works around with a two-phase save");
        });
    }

    [Test]
    public async Task Handle_EveryoneRoleIsUpdatedNotDuplicated()
    {
        var response = await Invoke(BasicCommand());

        var everyoneRoles = _context.Roles.Where(r => r.GuildId == response.GuildId && r.Type == RoleType.Everyone).ToList();
        Assert.That(everyoneRoles, Has.Count.EqualTo(1));
        Assert.That((ulong)everyoneRoles[0].Permissions, Is.EqualTo(0b11ul));
    }

    [Test]
    public async Task Handle_ChannelOverwriteResolvesMappedRoleId()
    {
        var response = await Invoke(BasicCommand());

        var echoRoleId = response.DiscordToEchoRoleIds["role-mod"];
        var echoChannelId = response.DiscordToEchoChannelIds["chan-1"];

        var overwrite = _context.Set<Guild.Domain.Entity.ChannelPermission>()
            .Single(p => p.ChannelId == echoChannelId);

        Assert.That(overwrite.RoleId, Is.EqualTo(echoRoleId));
    }

    [Test]
    public async Task Handle_UnmappedOverwriteRoleIsSkippedWithoutThrowing()
    {
        var command = BasicCommand();
        command.Categories[0].Channels[0].Overwrites.Add(
            new ImportedOverwriteDto { DiscordRoleId = "unknown-role", AllowPermissions = 1, DenyPermissions = 0 });

        var response = await Invoke(command);

        var echoChannelId = response.DiscordToEchoChannelIds["chan-1"];
        var overwrites = _context.Set<Guild.Domain.Entity.ChannelPermission>()
            .Where(p => p.ChannelId == echoChannelId).ToList();

        Assert.That(overwrites, Has.Count.EqualTo(1), "the unmapped role's overwrite should be silently dropped");
    }

    [Test]
    public async Task Handle_ChannelNameFailsDomainValidation_ReturnsErrorMessageInsteadOfThrowing()
    {
        // ChannelValidator rejects whitespace in a channel name - Import.Application sanitizes
        // this upstream, but this handler must still fail gracefully (not dead-letter the
        // Wolverine message) if a bad name gets through some other path.
        var command = BasicCommand();
        command.Categories[0].Channels[0].Name = "bad name with spaces";

        ImportGuildStructureResponse response = null!;
        Assert.DoesNotThrowAsync(async () => response = await Invoke(command));

        Assert.Multiple(() =>
        {
            Assert.That(response.ErrorMessage, Is.Not.Null);
            Assert.That(response.GuildId, Is.Null, "a failed build must not report a guild id");
            Assert.That(_context.Guilds.Any(), Is.False, "nothing partially built should be persisted on failure");
        });
    }

    /// <summary>ImportGuildStructureHandler deliberately doesn't call SaveChangesAsync itself
    /// (bus handlers auto-commit via Wolverine's DbContext middleware in production) - tests
    /// invoke it directly with no such middleware present, so this simulates that one commit.</summary>
    private async Task<ImportGuildStructureResponse> Invoke(ImportGuildStructureCommand command)
    {
        var response = await ImportGuildStructureHandler.Handle(command, _context, _auditLog, _bus, _hub,
            NullLogger<ImportGuildStructureHandler>.Instance);
        await _context.SaveChangesAsync();
        return response;
    }

    [Test]
    public async Task Handle_BroadcastsGuildCreatedToOwner()
    {
        await Invoke(BasicCommand());

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.That(hubClients.SentMessages, Has.Some.Matches<(string Method, object?[] Args)>(m => m.Method == "guild.GuildCreated"));
    }
}
