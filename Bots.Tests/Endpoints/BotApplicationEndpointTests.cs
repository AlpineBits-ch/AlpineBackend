using System.Security.Claims;
using Bots.Application.Dtos.Request;
using Bots.Application.Dtos.Response;
using Bots.Application.Endpoints;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Identity.Contracts.Bus.Commands;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Bots.Tests.Endpoints;

[TestFixture]
public class BotApplicationEndpointTests
{
    private TestBotsContext _context = null!;
    private FakeBotAccountMessageBus _bus = null!;
    private BotApplicationEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestBotsContext(Guid.NewGuid().ToString());
        _bus = new FakeBotAccountMessageBus();
        _endpoint = new BotApplicationEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ClaimsPrincipal MakeUser(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private async Task<BotApplication> AddApplicationAsync(string ownerUserId, string botUserId = "usr_bot1")
    {
        var app = new BotApplication { Id = BotApplication.GenerateId(), OwnerUserId = ownerUserId, BotUserId = botUserId, Name = "Test Bot" };
        _context.BotApplications.Add(app);
        await _context.SaveChangesAsync();
        return app;
    }

    // ── CreateBotApplicationAsync ────────────────────────────────────────────

    [Test]
    public async Task Create_ValidRequest_PersistsApplicationAndReturnsCreated()
    {
        _bus.CreateResponse = new CreateBotAccountResponse { BotUserId = "usr_bot", ClientSecret = "s3cr3t" };
        var dto = new CreateBotApplicationDto { Name = "My Bot", Description = "desc" };

        var result = await _endpoint.CreateBotApplicationAsync(dto, MakeUser("usr_owner"), _context, _bus);
        // The endpoint itself never calls SaveChangesAsync - in production Wolverine's DbContext
        // integration auto-commits after a Wolverine.Http endpoint returns; calling it directly
        // here (bypassing Wolverine) means the test must do that commit itself before querying.
        await _context.SaveChangesAsync();

        var created = (Created<BotApplicationCreatedDto>)result;
        Assert.That(created.Value!.ClientSecret, Is.EqualTo("s3cr3t"));
        Assert.That(created.Value.Name, Is.EqualTo("My Bot"));
        Assert.That(_context.BotApplications.Single().OwnerUserId, Is.EqualTo("usr_owner"));
    }

    [Test]
    public async Task Create_NoAuthenticatedUser_ReturnsUnauthorized()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await _endpoint.CreateBotApplicationAsync(new CreateBotApplicationDto { Name = "Bot" }, anonymous, _context, _bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
        Assert.That(_context.BotApplications.Any(), Is.False);
    }

    // ── GetOwnedBotApplicationsAsync ─────────────────────────────────────────

    [Test]
    public async Task GetOwned_ReturnsOnlyCallersApplications()
    {
        await AddApplicationAsync("usr_owner", "usr_bot1");
        await AddApplicationAsync("usr_someone_else", "usr_bot2");

        var result = await _endpoint.GetOwnedBotApplicationsAsync(MakeUser("usr_owner"), _context);

        var ok = (Ok<List<BotApplicationDto>>)result;
        Assert.That(ok.Value!.Single().BotUserId, Is.EqualTo("usr_bot1"));
    }

    // ── GetBotApplicationAsync ───────────────────────────────────────────────

    [Test]
    public async Task GetById_OwnedApplication_ReturnsIt()
    {
        var app = await AddApplicationAsync("usr_owner");

        var result = await _endpoint.GetBotApplicationAsync(app.Id, MakeUser("usr_owner"), _context);

        var ok = (Ok<BotApplicationDto>)result;
        Assert.That(ok.Value!.Id, Is.EqualTo(app.Id));
    }

    [Test]
    public async Task GetById_ApplicationOwnedByAnotherUser_ReturnsForbid()
    {
        var app = await AddApplicationAsync("usr_owner");

        var result = await _endpoint.GetBotApplicationAsync(app.Id, MakeUser("usr_intruder"), _context);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetById_UnknownApplication_ReturnsNotFound()
    {
        var result = await _endpoint.GetBotApplicationAsync("boap_missing", MakeUser("usr_owner"), _context);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    // ── DisableBotApplicationAsync ───────────────────────────────────────────

    [Test]
    public async Task Disable_OwnedApplication_DisablesAndSendsCommand()
    {
        var app = await AddApplicationAsync("usr_owner");

        var result = await _endpoint.DisableBotApplicationAsync(app.Id, MakeUser("usr_owner"), _context, _bus);

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(app.IsEnabled, Is.False);
        Assert.That(_bus.Invoked.Single(), Is.InstanceOf<DisableBotAccountCommand>());
    }

    [Test]
    public async Task Disable_ApplicationOwnedByAnotherUser_ReturnsForbidAndDoesNotDisable()
    {
        var app = await AddApplicationAsync("usr_owner");

        var result = await _endpoint.DisableBotApplicationAsync(app.Id, MakeUser("usr_intruder"), _context, _bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
        Assert.That(app.IsEnabled, Is.True);
        Assert.That(_bus.Invoked, Is.Empty);
    }

    // ── ResetBotSecretAsync ──────────────────────────────────────────────────

    [Test]
    public async Task ResetSecret_OwnedApplication_ReturnsNewSecret()
    {
        var app = await AddApplicationAsync("usr_owner");
        _bus.ResetResponse = new ResetBotSecretResponse { Found = true, ClientSecret = "brand-new-secret" };

        var result = await _endpoint.ResetBotSecretAsync(app.Id, MakeUser("usr_owner"), _context, _bus);

        var ok = (Ok<ResetSecretDto>)result;
        Assert.That(ok.Value!.ClientSecret, Is.EqualTo("brand-new-secret"));
        Assert.That(ok.Value.ClientId, Is.EqualTo(app.BotUserId));
    }

    [Test]
    public async Task ResetSecret_IdentityHasNoMatchingAccount_ReturnsNotFound()
    {
        var app = await AddApplicationAsync("usr_owner");
        _bus.ResetResponse = new ResetBotSecretResponse { Found = false };

        var result = await _endpoint.ResetBotSecretAsync(app.Id, MakeUser("usr_owner"), _context, _bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }
}
