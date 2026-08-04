using System.Text.Json;
using Identity.Application.Commands;
using Identity.Contracts.Bus.Commands;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Identity.Tests.Commands;

/// <summary>Identity's participant in the cross-service export fan-out (T1-7).</summary>
[TestFixture]
public class ExportUserDataCommandHandlerTests
{
    private TestIdentityContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestIdentityContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ApplicationUser SeedUser(TestIdentityContext ctx, string tag)
    {
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = $"{tag}-{Guid.NewGuid():N}@example.com",
            Username = $"{tag}{Guid.NewGuid():N}"[..12],
            PhoneNumber = null!,
            BirthDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-25)),
        });

        ctx.Users.Add(user);
        ctx.SaveChanges();
        return user;
    }

    private Task<Contracts.Bus.Response.ExportUserDataResponse> HandleAsync(string exportId, string userId) =>
        ExportUserDataCommandHandler.Handle(
            new ExportUserDataCommand { ExportId = exportId, UserId = userId },
            _context,
            NullLogger<ExportUserDataCommandHandler>.Instance);

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_IncludesTheSubjectsOwnAccountAndCountsItsRows()
    {
        var user = SeedUser(_context, "export");

        _context.LoginSessions.Add(LoginSession.Create(new CreateLoginSessionParams
        {
            UserId = user.Id,
            DeviceName = "Test Device",
            DeviceType = DeviceType.Desktop,
            IpAddress = "203.0.113.9",
            UserAgent = "TestAgent/1.0",
        }));
        await _context.SaveChangesAsync();

        var response = await HandleAsync("dxrq_normal", user.Id);

        Assert.That(response.Service, Is.EqualTo("identity"));
        Assert.That(response.ExportId, Is.EqualTo("dxrq_normal"));
        Assert.That(response.Error, Is.Null);
        Assert.That(response.RowCounts["account"], Is.EqualTo(1));
        Assert.That(response.RowCounts["loginSessions"], Is.EqualTo(1));

        using var document = JsonDocument.Parse(response.FragmentJson);
        var account = document.RootElement.GetProperty("account");

        Assert.That(account.GetProperty("email").GetString(), Is.EqualTo(user.Email));
        Assert.That(account.GetProperty("id").GetString(), Is.EqualTo(user.Id));
    }

    [Test]
    public async Task Handle_MovesAPendingRequestToRunning()
    {
        var user = SeedUser(_context, "running");

        var request = DataExportRequest.Create(user.Id, DateTimeOffset.UtcNow);
        _context.DataExportRequests.Add(request);
        await _context.SaveChangesAsync();

        await HandleAsync(request.Id, user.Id);
        await _context.SaveChangesAsync();

        var reloaded = await _context.DataExportRequests.FirstAsync(r => r.Id == request.Id);
        Assert.That(reloaded.Status, Is.EqualTo(DataExportStatus.Running));
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_UnknownAccount_ReportsAnErrorRatherThanThrowing()
    {
        var response = await HandleAsync("dxrq_missing", "user_doesnotexist");

        Assert.That(response.Service, Is.EqualTo("identity"));
        Assert.That(response.Error, Is.Not.Null);
        Assert.That(response.FragmentJson, Is.EqualTo("{}"));
    }

    [Test]
    public async Task Handle_AlreadyReadyRequest_IsNotMovedBackToRunning()
    {
        var user = SeedUser(_context, "ready");

        var request = DataExportRequest.Create(user.Id, DateTimeOffset.UtcNow);
        request.MarkReady("data-exports/x/y.zip", DateTimeOffset.UtcNow, TimeSpan.FromDays(7));
        _context.DataExportRequests.Add(request);
        await _context.SaveChangesAsync();

        await HandleAsync(request.Id, user.Id);
        await _context.SaveChangesAsync();

        var reloaded = await _context.DataExportRequests.FirstAsync(r => r.Id == request.Id);
        Assert.That(reloaded.Status, Is.EqualTo(DataExportStatus.Ready));
    }

    // ── negative: the whole point ───────────────────────────────────────────

    [Test]
    public async Task Handle_DoesNotLeakAnotherAccountsPersonalData()
    {
        var subject = SeedUser(_context, "subject");
        var other = SeedUser(_context, "other");

        other.PhoneNumber = "+15550001111";

        _context.LoginSessions.Add(LoginSession.Create(new CreateLoginSessionParams
        {
            UserId = other.Id,
            DeviceName = "Someone Else's Laptop",
            DeviceType = DeviceType.Desktop,
            IpAddress = "198.51.100.42",
            UserAgent = "OtherAgent/9.9",
        }));

        _context.IdentityAuditEvents.Add(IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = other.Id,
            Action = IdentityAuditActions.BackupRead,
            Detail = "somebody else's backup",
            IpAddress = "198.51.100.42",
        }));

        await _context.SaveChangesAsync();

        var response = await HandleAsync("dxrq_leak", subject.Id);

        Assert.Multiple(() =>
        {
            Assert.That(response.FragmentJson, Does.Not.Contain(other.Email!));
            Assert.That(response.FragmentJson, Does.Not.Contain("+15550001111"));
            Assert.That(response.FragmentJson, Does.Not.Contain("198.51.100.42"));
            Assert.That(response.FragmentJson, Does.Not.Contain("OtherAgent/9.9"));
            Assert.That(response.FragmentJson, Does.Not.Contain(other.Id));
        });
    }

    [Test]
    public async Task Handle_DoesNotExportTheSubjectsOwnCredentials()
    {
        var user = SeedUser(_context, "creds");

        _context.UserPushTokens.Add(UserPushToken.Create(new CreateUserPushTokenParams
        {
            UserId = user.Id,
            Token = "fcm-secret-token-value",
            Kind = PushTokenKind.Fcm,
        }));
        await _context.SaveChangesAsync();

        var response = await HandleAsync("dxrq_creds", user.Id);

        Assert.Multiple(() =>
        {
            // The registration is disclosed; the token that can push to the handset is not.
            Assert.That(response.RowCounts["pushRegistrations"], Is.EqualTo(1));
            Assert.That(response.FragmentJson, Does.Not.Contain("fcm-secret-token-value"));
            Assert.That(response.FragmentJson, Does.Not.Contain("PasswordHash"));
            Assert.That(response.FragmentJson, Does.Not.Contain("SecurityStamp"));
        });
    }
}
