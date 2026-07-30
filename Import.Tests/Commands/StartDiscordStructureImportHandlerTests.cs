using System.Net;
using System.Net.Http.Json;
using Guild.Contracts.Bus.Commands;
using Import.Application.Commands;
using Import.Application.Discord;
using Import.Domain.Entity;
using Import.Domain.Enums;
using Import.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Import.Tests.Commands;

[TestFixture]
public class StartDiscordStructureImportHandlerTests
{
    private const string DiscordGuildId = "discord-guild-1";
    private const string RequestedByUserId = "usr_requester";

    private TestImportContext _context = null!;
    private FakeStructureImportBus _bus = null!;
    private QueuedHttpMessageHandler _handler = null!;
    private DiscordApiClient _discordApi = null!;
    private ImportJob _job = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestImportContext(Guid.NewGuid().ToString());
        _bus = new FakeStructureImportBus();
        _handler = new QueuedHttpMessageHandler();
        _discordApi = new DiscordApiClient(new FakeHttpClientFactory(_handler), NullLogger<DiscordApiClient>.Instance);

        _job = new ImportJob
        {
            Id = ImportJob.GenerateId(),
            DiscordGuildId = DiscordGuildId,
            RequestedByUserId = RequestedByUserId,
            Status = ImportJobStatus.Pending,
        };
        _context.ImportJobs.Add(_job);
        _context.SaveChanges();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.DisposeAsync();
        _handler.Dispose();
    }

    private StartDiscordStructureImportCommand Command() => new()
    {
        ImportJobId = _job.Id,
        DiscordGuildId = DiscordGuildId,
        RequestedByUserId = RequestedByUserId,
    };

    private void EnqueueGuild(string id = DiscordGuildId, string name = "My Server") =>
        _handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new DiscordGuildPayload { Id = id, Name = name })
        });

    private void EnqueueRoles(params DiscordRolePayload[] roles) =>
        _handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(roles.ToList()) });

    private void EnqueueChannels(params DiscordChannelPayload[] channels) =>
        _handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(channels.ToList()) });

    // ── Guard clauses ────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_DiscordGuildAlreadyLinked_MarksJobFailedWithoutCallingDiscord()
    {
        _context.GuildLinks.Add(new GuildLink
        {
            Id = GuildLink.GenerateId(), EchoGuildId = "gld_existing", DiscordGuildId = DiscordGuildId,
            SyncDirection = SyncDirection.DiscordToVenta, Status = GuildLinkStatus.Active,
        });
        await _context.SaveChangesAsync();

        await StartDiscordStructureImportHandler.Handle(Command(), _context, _discordApi, _bus, NullLogger<StartDiscordStructureImportHandler>.Instance, default);

        Assert.That(_job.Status, Is.EqualTo(ImportJobStatus.Failed));
        Assert.That(_job.EchoGuildId, Is.EqualTo("gld_existing"));
        Assert.That(_job.ErrorMessage, Does.Contain("already linked"));
        Assert.That(_handler.Requests, Is.Empty, "Should short-circuit before ever calling Discord");
    }

    [Test]
    public async Task Handle_BotNotInGuild_MarksJobFailed()
    {
        _handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.NotFound));

        await StartDiscordStructureImportHandler.Handle(Command(), _context, _discordApi, _bus, NullLogger<StartDiscordStructureImportHandler>.Instance, default);

        Assert.That(_job.Status, Is.EqualTo(ImportJobStatus.Failed));
        Assert.That(_job.ErrorMessage, Does.Contain("not a member"));
    }

    [Test]
    public async Task Handle_GuildStructureCommandReturnsError_MarksJobFailed()
    {
        EnqueueGuild();
        EnqueueRoles();
        EnqueueChannels();
        _bus.Response = new ImportGuildStructureResponse { ErrorMessage = "Name is invalid" };

        await StartDiscordStructureImportHandler.Handle(Command(), _context, _discordApi, _bus, NullLogger<StartDiscordStructureImportHandler>.Instance, default);

        Assert.That(_job.Status, Is.EqualTo(ImportJobStatus.Failed));
        Assert.That(_job.ErrorMessage, Is.EqualTo("Name is invalid"));
        Assert.That(_context.GuildLinks.Any(), Is.False, "A failed structure command must not create a GuildLink");
    }

    [Test]
    public async Task Handle_DiscordApiThrows_CatchesAndMarksJobFailedWithExceptionMessage()
    {
        _handler.Enqueue(() => throw new HttpRequestException("network unreachable"));

        await StartDiscordStructureImportHandler.Handle(Command(), _context, _discordApi, _bus, NullLogger<StartDiscordStructureImportHandler>.Instance, default);

        Assert.That(_job.Status, Is.EqualTo(ImportJobStatus.Failed));
        Assert.That(_job.ErrorMessage, Is.EqualTo("network unreachable"));
    }

    // ── Happy path ───────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_Success_MarksJobCompletedAndCreatesActiveGuildLink()
    {
        EnqueueGuild();
        EnqueueRoles();
        EnqueueChannels();
        _bus.Response = new ImportGuildStructureResponse { GuildId = "gld_new" };

        await StartDiscordStructureImportHandler.Handle(Command(), _context, _discordApi, _bus, NullLogger<StartDiscordStructureImportHandler>.Instance, default);
        // Handle() itself never calls SaveChangesAsync - in production Wolverine's DbContext
        // integration auto-commits after a bus-dispatched Handle() returns; calling it directly
        // here (bypassing Wolverine) means the test must do that commit itself before querying.
        await _context.SaveChangesAsync();

        Assert.That(_job.Status, Is.EqualTo(ImportJobStatus.Completed));
        Assert.That(_job.EchoGuildId, Is.EqualTo("gld_new"));
        Assert.That(_job.CompletedAt, Is.Not.Null);

        var link = _context.GuildLinks.Single();
        Assert.That(link.EchoGuildId, Is.EqualTo("gld_new"));
        Assert.That(link.DiscordGuildId, Is.EqualTo(DiscordGuildId));
        Assert.That(link.Status, Is.EqualTo(GuildLinkStatus.Active));
    }

    [Test]
    public async Task Handle_Success_RecordsEntityMappingsForCategoriesChannelsAndRoles()
    {
        EnqueueGuild();
        EnqueueRoles();
        EnqueueChannels();
        _bus.Response = new ImportGuildStructureResponse
        {
            GuildId = "gld_new",
            DiscordToEchoCategoryIds = new Dictionary<string, string> { ["cat-1"] = "echo-cat-1" },
            DiscordToEchoChannelIds = new Dictionary<string, string> { ["chan-1"] = "echo-chan-1" },
            DiscordToEchoRoleIds = new Dictionary<string, string> { ["role-1"] = "echo-role-1" },
        };

        await StartDiscordStructureImportHandler.Handle(Command(), _context, _discordApi, _bus, NullLogger<StartDiscordStructureImportHandler>.Instance, default);
        await _context.SaveChangesAsync();

        Assert.That(_context.ImportEntityMappings.Count(), Is.EqualTo(3));
        Assert.That(_context.ImportEntityMappings.Single(m => m.DiscordId == "cat-1").EntityType, Is.EqualTo(ImportEntityType.Category));
        Assert.That(_context.ImportEntityMappings.Single(m => m.DiscordId == "chan-1").EntityType, Is.EqualTo(ImportEntityType.Channel));
        Assert.That(_context.ImportEntityMappings.Single(m => m.DiscordId == "role-1").EntityType, Is.EqualTo(ImportEntityType.Role));
    }

    [Test]
    public async Task Handle_Success_BuildsImportCommandWithEveryoneRoleFlagged()
    {
        EnqueueGuild(); // Id defaults to DiscordGuildId
        EnqueueRoles(
            new DiscordRolePayload { Id = DiscordGuildId, Name = "@everyone", Permissions = "0" },
            new DiscordRolePayload { Id = "role-mod", Name = "Moderator", Permissions = "8" });
        EnqueueChannels();

        await StartDiscordStructureImportHandler.Handle(Command(), _context, _discordApi, _bus, NullLogger<StartDiscordStructureImportHandler>.Instance, default);

        var sentCommand = (ImportGuildStructureCommand)_bus.Invoked.Single(m => m is ImportGuildStructureCommand);
        var everyoneRole = sentCommand.Roles.Single(r => r.DiscordId == DiscordGuildId);
        var modRole = sentCommand.Roles.Single(r => r.DiscordId == "role-mod");
        Assert.That(everyoneRole.IsEveryoneRole, Is.True);
        Assert.That(modRole.IsEveryoneRole, Is.False);
    }

    [Test]
    public async Task Handle_Success_SanitizesChannelNamesWithWhitespace()
    {
        EnqueueGuild();
        EnqueueRoles();
        EnqueueChannels(new DiscordChannelPayload { Id = "c1", Name = "my cool channel", Type = 0, Position = 0 });

        await StartDiscordStructureImportHandler.Handle(Command(), _context, _discordApi, _bus, NullLogger<StartDiscordStructureImportHandler>.Instance, default);

        var sentCommand = (ImportGuildStructureCommand)_bus.Invoked.Single(m => m is ImportGuildStructureCommand);
        var uncategorized = sentCommand.Categories.Single();
        Assert.That(uncategorized.Channels.Single().Name, Is.EqualTo("my-cool-channel"));
    }

    [Test]
    public async Task Handle_Success_UncategorizedChannelsGroupedUnderSyntheticCategory()
    {
        EnqueueGuild();
        EnqueueRoles();
        EnqueueChannels(new DiscordChannelPayload { Id = "c1", Name = "general", Type = 0, Position = 0, ParentId = null });

        await StartDiscordStructureImportHandler.Handle(Command(), _context, _discordApi, _bus, NullLogger<StartDiscordStructureImportHandler>.Instance, default);

        var sentCommand = (ImportGuildStructureCommand)_bus.Invoked.Single(m => m is ImportGuildStructureCommand);
        Assert.That(sentCommand.Categories, Has.Count.EqualTo(1));
        Assert.That(sentCommand.Categories[0].Name, Is.EqualTo("Channels"));
        Assert.That(sentCommand.Categories[0].Channels.Single().DiscordId, Is.EqualTo("c1"));
    }

    [Test]
    public async Task Handle_Success_ThreadChannelsAreExcludedFromImportCommand()
    {
        EnqueueGuild();
        EnqueueRoles();
        EnqueueChannels(
            new DiscordChannelPayload { Id = "c1", Name = "general", Type = 0, Position = 0 },
            new DiscordChannelPayload { Id = "thread-1", Name = "a thread", Type = 11, Position = 1, ParentId = "c1" });

        await StartDiscordStructureImportHandler.Handle(Command(), _context, _discordApi, _bus, NullLogger<StartDiscordStructureImportHandler>.Instance, default);

        var sentCommand = (ImportGuildStructureCommand)_bus.Invoked.Single(m => m is ImportGuildStructureCommand);
        var allChannelIds = sentCommand.Categories.SelectMany(c => c.Channels).Select(c => c.DiscordId).ToList();
        Assert.That(allChannelIds, Does.Not.Contain("thread-1"));
    }

    [Test]
    public async Task Handle_Success_MemberTargetedOverwritesAreDropped()
    {
        EnqueueGuild();
        EnqueueRoles();
        EnqueueChannels(new DiscordChannelPayload
        {
            Id = "c1", Name = "general", Type = 0, Position = 0,
            PermissionOverwrites =
            [
                new DiscordOverwritePayload { Id = "role-1", Type = 0, Allow = "1024" },
                new DiscordOverwritePayload { Id = "member-1", Type = 1, Allow = "1024" },
            ],
        });

        await StartDiscordStructureImportHandler.Handle(Command(), _context, _discordApi, _bus, NullLogger<StartDiscordStructureImportHandler>.Instance, default);

        var sentCommand = (ImportGuildStructureCommand)_bus.Invoked.Single(m => m is ImportGuildStructureCommand);
        var overwrites = sentCommand.Categories.SelectMany(c => c.Channels).Single().Overwrites;
        Assert.That(overwrites, Has.Count.EqualTo(1));
        Assert.That(overwrites[0].DiscordRoleId, Is.EqualTo("role-1"));
    }
}
