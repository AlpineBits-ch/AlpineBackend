using System.Security.Claims;
using Echo.Controllers.Admin;
using Echo.Domain.Entities.Moderation;
using Echo.Moderation;
using Echo.Persistence.Persistance;
using Echo.Tests.Support;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;

namespace Echo.Tests.Controllers;

/// <summary>The console's email change: admin only, Identity's refusals mapped, and audited once with both addresses.</summary>
[TestFixture]
[Category("Unit")]
public class AdminUserEmailTests
{
    private sealed class TestContext() : MicroserviceContext(
        new DbContextOptionsBuilder<MicroserviceContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
        }
    }

    private sealed class FakeBus(string role) : IMessageBus
    {
        public SetUserEmailResponse? Answer { get; set; }
        public bool IdentityUnavailable { get; set; }
        public SetUserEmailRequest? LastRequest { get; private set; }

        public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
        {
            if (message is IsUserAdministrativeRequest)
            {
                object staff = new IsUserAdministrativeResponse
                {
                    Role = role,
                    IsAdministrative = role == "Admin",
                    IsStaff = role is "Admin" or "Moderator",
                    UserName = "staff",
                };
                return Task.FromResult((T)staff);
            }

            if (message is SetUserEmailRequest request)
            {
                LastRequest = request;
                if (IdentityUnavailable) throw new TimeoutException("identity did not answer");
                return Task.FromResult((T)(object)Answer!);
            }

            throw new NotSupportedException(message.GetType().Name);
        }

        public Guid? CorrelationId => null;
        public string? TenantId { get; set; }
        public Task InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = null) => Task.CompletedTask;
        public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null) => ValueTask.CompletedTask;
        public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null) => throw new NotImplementedException();
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

    private static (AdminUsersController Controller, FakeBus Bus, TestContext Db) Console(string role)
    {
        var bus = new FakeBus(role);
        var db = new TestContext();
        var controller = new AdminUsersController(
            db,
            new StaffAccess(bus, new RecordingLogger<StaffAccess>()),
            bus,
            new ModerationMailer(null!, new RecordingLogger<ModerationMailer>()),
            new RecordingLogger<AdminUsersController>());

        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user_actor")]));
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        return (controller, bus, db);
    }

    private static (int Status, string? Code) Refusal(IActionResult result)
    {
        var payload = (ObjectResult)result;
        var value = payload.Value!;
        return (payload.StatusCode!.Value, value.GetType().GetProperty("code")?.GetValue(value) as string);
    }

    private static Task<IActionResult> Set(AdminUsersController console, string email) =>
        console.SetEmailAsync("user_target", new SetEmailRequest { Email = email }, CancellationToken.None);

    [Test]
    public async Task A_moderator_is_refused_and_nothing_reaches_identity()
    {
        var (console, bus, db) = Console("Moderator");

        var result = Refusal(await Set(console, "new@example.com"));

        Assert.Multiple(async () =>
        {
            Assert.That(result.Status, Is.EqualTo(StatusCodes.Status403Forbidden));
            Assert.That(result.Code, Is.EqualTo("admin_required"));
            Assert.That(bus.LastRequest, Is.Null);
            Assert.That(await db.ModerationAuditEntries.CountAsync(), Is.Zero);
        });
    }

    [Test]
    public async Task A_change_is_audited_with_both_addresses_against_the_resolved_actor()
    {
        var (console, bus, db) = Console("Admin");
        bus.Answer = new SetUserEmailResponse
        {
            Success = true,
            Email = "new@example.com",
            PreviousEmail = "old@example.com",
            SessionsRevoked = 2,
        };

        var result = (OkObjectResult)await Set(console, "new@example.com");
        var entry = await db.ModerationAuditEntries.SingleAsync();
        var email = result.Value!.GetType().GetProperty("email")!.GetValue(result.Value) as string;

        Assert.Multiple(() =>
        {
            Assert.That(bus.LastRequest!.ActorUserId, Is.EqualTo("user_actor"));
            Assert.That(email, Is.EqualTo("new@example.com"));
            Assert.That(entry.Action, Is.EqualTo(ModerationAuditActions.EmailChanged));
            Assert.That(entry.ActorUserId, Is.EqualTo("user_actor"));
            Assert.That(entry.SubjectId, Is.EqualTo("user_target"));
            Assert.That(entry.Detail, Is.EqualTo("old@example.com -> new@example.com"));
        });
    }

    [TestCase("email_taken", StatusCodes.Status409Conflict)]
    [TestCase("invalid_email", StatusCodes.Status400BadRequest)]
    [TestCase("not_found", StatusCodes.Status404NotFound)]
    public async Task An_identity_refusal_keeps_its_code_and_writes_no_audit(string code, int status)
    {
        var (console, bus, db) = Console("Admin");
        bus.Answer = new SetUserEmailResponse { Success = false, FailureCode = code, FailureMessage = "no" };

        var result = Refusal(await Set(console, "taken@example.com"));

        Assert.Multiple(async () =>
        {
            Assert.That(result.Status, Is.EqualTo(status));
            Assert.That(result.Code, Is.EqualTo(code));
            Assert.That(await db.ModerationAuditEntries.CountAsync(), Is.Zero);
        });
    }

    [Test]
    public async Task The_same_address_again_writes_no_audit()
    {
        var (console, bus, db) = Console("Admin");
        bus.Answer = new SetUserEmailResponse
        {
            Success = true, FailureCode = "no_change", Email = "same@example.com", PreviousEmail = "same@example.com",
        };

        Assert.That(await Set(console, "same@example.com"), Is.InstanceOf<OkObjectResult>());
        Assert.That(await db.ModerationAuditEntries.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task An_identity_outage_is_a_503()
    {
        var (console, bus, _) = Console("Admin");
        bus.IdentityUnavailable = true;

        var result = Refusal(await Set(console, "new@example.com"));

        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
        Assert.That(result.Code, Is.EqualTo("identity_unavailable"));
    }
}
