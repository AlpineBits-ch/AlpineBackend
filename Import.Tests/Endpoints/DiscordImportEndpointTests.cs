using System.Net;
using System.Security.Claims;
using Guild.Contracts;
using Guild.Contracts.Bus.Response;
using Import.Application.Commands;
using Import.Application.Discord;
using Import.Application.Endpoints;
using Import.Application.Redis;
using Import.Domain.Entity;
using Import.Domain.Enums;
using Import.Tests.Helpers;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Import.Tests.Endpoints;

[TestFixture]
public class DiscordImportEndpointTests
{
    private TestImportContext _context = null!;
    private DiscordImportStateStore _stateStore = null!;
    private FakeStructureImportBus _bus = null!;
    private DiscordImportEndpoint _endpoint = null!;
    private QueuedHttpMessageHandler _handler = null!;
    private DiscordApiClient _discordApi = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestImportContext(Guid.NewGuid().ToString());

        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        IDistributedCache cache = services.BuildServiceProvider().GetRequiredService<IDistributedCache>();
        _stateStore = new DiscordImportStateStore(cache);

        _bus = new FakeStructureImportBus();
        _handler = new QueuedHttpMessageHandler();
        _discordApi = new DiscordApiClient(new FakeHttpClientFactory(_handler), NullLogger<DiscordApiClient>.Instance);
        _endpoint = new DiscordImportEndpoint();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.DisposeAsync();
        _handler.Dispose();
    }

    private static ClaimsPrincipal MakeUser(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    // ── Callback ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Callback_ValidState_CreatesJobAndSendsCommand()
    {
        await _stateStore.SaveAsync("state-1", "usr_requester");

        var result = await _endpoint.Callback("state-1", "discord-guild-1", _stateStore, _context, _bus);

        Assert.That(result, Is.InstanceOf<RedirectHttpResult>());
        var job = _context.ImportJobs.Single();
        Assert.That(job.DiscordGuildId, Is.EqualTo("discord-guild-1"));
        Assert.That(job.RequestedByUserId, Is.EqualTo("usr_requester"));
        Assert.That(job.Status, Is.EqualTo(ImportJobStatus.Pending));

        var sent = (StartDiscordStructureImportCommand)_bus.Invoked.Single();
        Assert.That(sent.ImportJobId, Is.EqualTo(job.Id));
    }

    [Test]
    public async Task Callback_UnknownState_ReturnsBadRequestAndCreatesNoJob()
    {
        var result = await _endpoint.Callback("never-saved", "discord-guild-1", _stateStore, _context, _bus);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        Assert.That(_context.ImportJobs.Any(), Is.False);
    }

    [Test]
    public async Task Callback_MissingGuildId_ReturnsBadRequest()
    {
        await _stateStore.SaveAsync("state-1", "usr_requester");

        var result = await _endpoint.Callback("state-1", null, _stateStore, _context, _bus);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Callback_StateIsConsumedExactlyOnce_SecondCallbackWithSameStateFails()
    {
        await _stateStore.SaveAsync("state-1", "usr_requester");

        await _endpoint.Callback("state-1", "discord-guild-1", _stateStore, _context, _bus);
        var second = await _endpoint.Callback("state-1", "discord-guild-1", _stateStore, _context, _bus);

        Assert.That(second, Is.InstanceOf<BadRequest<string>>());
        Assert.That(_context.ImportJobs.Count(), Is.EqualTo(1));
    }

    // ── GetStatus ────────────────────────────────────────────────────────────

    [Test]
    public async Task GetStatus_OwnedJob_ReturnsJobStatus()
    {
        var job = new ImportJob { Id = ImportJob.GenerateId(), DiscordGuildId = "d1", RequestedByUserId = "usr_1", Status = ImportJobStatus.CreatingGuild };
        _context.ImportJobs.Add(job);
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetStatus(job.Id, _context, MakeUser("usr_1"));

        var ok = (Ok<ImportJobStatusDto>)result;
        Assert.That(ok.Value!.Status, Is.EqualTo("CreatingGuild"));
    }

    [Test]
    public async Task GetStatus_JobOwnedByAnotherUser_ReturnsNotFound()
    {
        var job = new ImportJob { Id = ImportJob.GenerateId(), DiscordGuildId = "d1", RequestedByUserId = "usr_owner", Status = ImportJobStatus.Pending };
        _context.ImportJobs.Add(job);
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetStatus(job.Id, _context, MakeUser("usr_someone_else"));

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetStatus_UnknownJobId_ReturnsNotFound()
    {
        var result = await _endpoint.GetStatus("imjb_missing", _context, MakeUser("usr_1"));

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    // ── GetLinks ─────────────────────────────────────────────────────────────

    [Test]
    public async Task GetLinks_LinkedGuild_ReturnsLinkInfo()
    {
        _context.GuildLinks.Add(new GuildLink
        {
            Id = GuildLink.GenerateId(), EchoGuildId = "gld_1", DiscordGuildId = "d1",
            SyncDirection = SyncDirection.DiscordToVenta, Status = GuildLinkStatus.Active,
        });
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetLinks("gld_1", _context, _bus, MakeUser("usr_admin"));

        var ok = (Ok<GuildLinkDto[]>)result;
        Assert.That(ok.Value!.Single().DiscordGuildId, Is.EqualTo("d1"));
    }

    [Test]
    public async Task GetLinks_UnlinkedGuild_ReturnsEmptyArray()
    {
        var result = await _endpoint.GetLinks("gld_unlinked", _context, _bus, MakeUser("usr_admin"));

        var ok = (Ok<GuildLinkDto[]>)result;
        Assert.That(ok.Value, Is.Empty);
    }

    // ── SetLinkStatus ────────────────────────────────────────────────────────

    [Test]
    public async Task SetLinkStatus_ValidTransitionToPaused_UpdatesStatus()
    {
        var link = new GuildLink { Id = GuildLink.GenerateId(), EchoGuildId = "gld_1", DiscordGuildId = "d1", SyncDirection = SyncDirection.DiscordToVenta, Status = GuildLinkStatus.Active };
        _context.GuildLinks.Add(link);
        await _context.SaveChangesAsync();

        var result = await _endpoint.SetLinkStatus(link.Id, new SetLinkStatusDto { Status = "Paused" }, _context, _bus, MakeUser("usr_admin"));

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(link.Status, Is.EqualTo(GuildLinkStatus.Paused));
    }

    [Test]
    public async Task SetLinkStatus_AttemptToSetRevoked_ReturnsBadRequest()
    {
        var link = new GuildLink { Id = GuildLink.GenerateId(), EchoGuildId = "gld_1", DiscordGuildId = "d1", SyncDirection = SyncDirection.DiscordToVenta, Status = GuildLinkStatus.Active };
        _context.GuildLinks.Add(link);
        await _context.SaveChangesAsync();

        var result = await _endpoint.SetLinkStatus(link.Id, new SetLinkStatusDto { Status = "Revoked" }, _context, _bus, MakeUser("usr_admin"));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        Assert.That(link.Status, Is.EqualTo(GuildLinkStatus.Active), "Rejected transition must not mutate the link");
    }

    [Test]
    public async Task SetLinkStatus_InvalidStatusString_ReturnsBadRequest()
    {
        var link = new GuildLink { Id = GuildLink.GenerateId(), EchoGuildId = "gld_1", DiscordGuildId = "d1", SyncDirection = SyncDirection.DiscordToVenta, Status = GuildLinkStatus.Active };
        _context.GuildLinks.Add(link);
        await _context.SaveChangesAsync();

        var result = await _endpoint.SetLinkStatus(link.Id, new SetLinkStatusDto { Status = "NotARealStatus" }, _context, _bus, MakeUser("usr_admin"));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task SetLinkStatus_UnknownLinkId_ReturnsNotFound()
    {
        var result = await _endpoint.SetLinkStatus("glnk_missing", new SetLinkStatusDto { Status = "Paused" }, _context, _bus, MakeUser("usr_admin"));

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    // ── Unlink ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Unlink_KnownLink_MarksRevokedAndCallsLeaveGuild()
    {
        var link = new GuildLink { Id = GuildLink.GenerateId(), EchoGuildId = "gld_1", DiscordGuildId = "d1", SyncDirection = SyncDirection.DiscordToVenta, Status = GuildLinkStatus.Active };
        _context.GuildLinks.Add(link);
        await _context.SaveChangesAsync();
        _handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await _endpoint.Unlink(link.Id, _context, _discordApi, _bus, MakeUser("usr_admin"));

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(link.Status, Is.EqualTo(GuildLinkStatus.Revoked));
        Assert.That(_handler.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Unlink_DiscordApiFails_StillRevokesLinkBestEffort()
    {
        var link = new GuildLink { Id = GuildLink.GenerateId(), EchoGuildId = "gld_1", DiscordGuildId = "d1", SyncDirection = SyncDirection.DiscordToVenta, Status = GuildLinkStatus.Active };
        _context.GuildLinks.Add(link);
        await _context.SaveChangesAsync();
        _handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await _endpoint.Unlink(link.Id, _context, _discordApi, _bus, MakeUser("usr_admin"));

        Assert.That(result, Is.InstanceOf<NoContent>(), "Discord API failure must be swallowed - the guild admin may have already removed the bot");
        Assert.That(link.Status, Is.EqualTo(GuildLinkStatus.Revoked));
    }

    [Test]
    public async Task Unlink_UnknownLinkId_ReturnsNotFound()
    {
        var result = await _endpoint.Unlink("glnk_missing", _context, _discordApi, _bus, MakeUser("usr_admin"));

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    // ── Authorization on the link routes ───────────────────────────────────── These three used to
    // take no caller identity at all, relying on a gateway check that does not exist (no YARP route
    // in this repo sets an AuthorizationPolicy).

    [Test]
    public async Task Unlink_CallerLacksManageGuild_IsForbiddenAndLeavesTheLinkIntact()
    {
        var link = new GuildLink { Id = GuildLink.GenerateId(), EchoGuildId = "gld_1", DiscordGuildId = "d1", SyncDirection = SyncDirection.DiscordToVenta, Status = GuildLinkStatus.Active };
        _context.GuildLinks.Add(link);
        await _context.SaveChangesAsync();
        _bus.GuildPermissionResponse = new HasUserPermissionToGuildResponse { IsAllowed = false, Permission = ExternalPermission.ManageGuild };

        var result = await _endpoint.Unlink(link.Id, _context, _discordApi, _bus, MakeUser("usr_outsider"));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(link.Status, Is.EqualTo(GuildLinkStatus.Active));
            Assert.That(_handler.Requests, Is.Empty, "the bot must not be made to leave the Discord server");
        });
    }

    [Test]
    public async Task SetLinkStatus_CallerLacksManageGuild_IsForbidden()
    {
        var link = new GuildLink { Id = GuildLink.GenerateId(), EchoGuildId = "gld_1", DiscordGuildId = "d1", SyncDirection = SyncDirection.DiscordToVenta, Status = GuildLinkStatus.Active };
        _context.GuildLinks.Add(link);
        await _context.SaveChangesAsync();
        _bus.GuildPermissionResponse = new HasUserPermissionToGuildResponse { IsAllowed = false, Permission = ExternalPermission.ManageGuild };

        var result = await _endpoint.SetLinkStatus(link.Id, new SetLinkStatusDto { Status = "Paused" }, _context, _bus, MakeUser("usr_outsider"));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(link.Status, Is.EqualTo(GuildLinkStatus.Active));
        });
    }

    [Test]
    public async Task GetLinks_CallerLacksManageGuild_IsForbidden()
    {
        // Also the id oracle for the two routes above.
        _bus.GuildPermissionResponse = new HasUserPermissionToGuildResponse { IsAllowed = false, Permission = ExternalPermission.ManageGuild };

        var result = await _endpoint.GetLinks("gld_1", _context, _bus, MakeUser("usr_outsider"));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }
}
