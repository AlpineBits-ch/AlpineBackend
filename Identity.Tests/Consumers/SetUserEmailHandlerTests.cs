using Alba;
using Identity.Application.Consumers;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Identity.Tests.Consumers;

/// <summary>An administrator replacing an account's sign-in address.</summary>
[TestFixture]
public class SetUserEmailHandlerTests
{
    private static IAlbaHost Host => AppFixture.Host;

    private IServiceScope _scope = null!;
    private MicroserviceContext _ctx = null!;
    private SetUserEmailHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _scope = Host.Services.CreateScope();
        _ctx = _scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        _handler = new SetUserEmailHandler();
    }

    [TearDown]
    public async Task TearDown()
    {
        _ctx.ChangeTracker.Clear();
        _ctx.LoginSessions.RemoveRange(_ctx.LoginSessions);
        _ctx.IdentityAuditEvents.RemoveRange(_ctx.IdentityAuditEvents);
        await _ctx.SaveChangesAsync();

        _ctx.Users.RemoveRange(_ctx.Users);
        await _ctx.SaveChangesAsync();

        _scope.Dispose();
    }

    private async Task<ApplicationUser> SeedAsync(
        UserType type = UserType.Default, UserStatus status = UserStatus.Active, string? email = null)
    {
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = email ?? $"mail-{Guid.NewGuid():N}@example.com",
            PhoneNumber = $"+4179{Random.Shared.Next(1000000, 9999999)}",
            Username = $"em{Guid.NewGuid():N}"[..15],
            BirthDate = new DateOnly(1990, 1, 1),
        });

        user.UserType = type;
        user.Status = status;

        _ctx.Users.Add(user);
        await _ctx.SaveChangesAsync();
        return user;
    }

    private async Task<LoginSession> SeedSessionAsync(string userId)
    {
        var session = LoginSession.Create(new CreateLoginSessionParams
        {
            UserId = userId,
            DeviceName = "test",
            DeviceType = default,
        });

        _ctx.LoginSessions.Add(session);
        await _ctx.SaveChangesAsync();
        return session;
    }

    /// <summary>Commits the way Wolverine's middleware would.</summary>
    private async Task<SetUserEmailResponse> SetAsync(string userId, string actorId, string email)
    {
        var response = await _handler.Handle(
            new SetUserEmailRequest { UserId = userId, ActorUserId = actorId, Email = email },
            _ctx,
            NullLogger<SetUserEmailHandler>.Instance);

        await _ctx.SaveChangesAsync();
        return response;
    }

    private Task<ApplicationUser> ReloadAsync(string id) =>
        _ctx.Users.AsNoTracking().SingleAsync(u => u.Id == id);

    [Test]
    public async Task Handle_ReplacesTheAddressAndMarksItConfirmed()
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync();
        var oldAddress = target.Email;
        var oldStamp = target.SecurityStamp;

        var response = await SetAsync(target.Id, admin.Id, "  New.Address@Example.com ");
        var stored = await ReloadAsync(target.Id);

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.FailureCode, Is.Null);
            Assert.That(response.Email, Is.EqualTo("New.Address@Example.com"));
            Assert.That(response.PreviousEmail, Is.EqualTo(oldAddress));
            Assert.That(stored.Email, Is.EqualTo("New.Address@Example.com"));
            Assert.That(stored.NormalizedEmail, Is.EqualTo("NEW.ADDRESS@EXAMPLE.COM"));
            Assert.That(stored.EmailConfirmed, Is.True);
            Assert.That(stored.EmailVerifiedAt, Is.Not.Null);
            Assert.That(stored.SecurityStamp, Is.Not.EqualTo(oldStamp));
        });
    }

    [Test]
    public async Task Handle_RevokesLiveSessionsAndAuditsBoth()
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync();
        await SeedSessionAsync(target.Id);
        await SeedSessionAsync(target.Id);

        var response = await SetAsync(target.Id, admin.Id, $"moved-{Guid.NewGuid():N}@example.com");

        var live = await _ctx.LoginSessions.AsNoTracking()
            .CountAsync(s => s.UserId == target.Id && s.RevokedAt == null);
        var audit = await _ctx.IdentityAuditEvents.AsNoTracking()
            .Where(e => e.UserId == target.Id)
            .Select(e => e.Action)
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.SessionsRevoked, Is.EqualTo(2));
            Assert.That(live, Is.Zero);
            Assert.That(audit, Does.Contain(IdentityAuditActions.EmailChangedByStaff));
            Assert.That(audit, Does.Contain(IdentityAuditActions.SessionsRevoked));
        });
    }

    [Test]
    public async Task Handle_RefusesAnAddressAnotherAccountUsesWhateverItsCase()
    {
        var admin = await SeedAsync(UserType.Admin);
        var holder = await SeedAsync(email: $"held-{Guid.NewGuid():N}@example.com");
        var target = await SeedAsync();
        var original = target.Email;

        var response = await SetAsync(target.Id, admin.Id, holder.Email!.ToUpperInvariant());

        Assert.Multiple(async () =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.FailureCode, Is.EqualTo("email_taken"));
            Assert.That((await ReloadAsync(target.Id)).Email, Is.EqualTo(original));
        });
    }

    [TestCase("not-an-address")]
    [TestCase("   ")]
    public async Task Handle_RefusesAMalformedAddress(string email)
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync();

        var response = await SetAsync(target.Id, admin.Id, email);

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.FailureCode, Is.EqualTo("invalid_email"));
        });
    }

    [Test]
    public async Task Handle_RefusesTheActorsOwnAccount()
    {
        var admin = await SeedAsync(UserType.Admin);

        var response = await SetAsync(admin.Id, admin.Id, $"self-{Guid.NewGuid():N}@example.com");

        Assert.That(response.FailureCode, Is.EqualTo("self_action"));
    }

    [Test]
    public async Task Handle_RefusesAnAccountBeingDeleted()
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync(status: UserStatus.PendingDeletion);

        var response = await SetAsync(target.Id, admin.Id, $"late-{Guid.NewGuid():N}@example.com");

        Assert.That(response.FailureCode, Is.EqualTo("invalid_state"));
    }

    [Test]
    public async Task Handle_TheSameAddressIsANoChangeThatKeepsSessions()
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync();
        await SeedSessionAsync(target.Id);

        var response = await SetAsync(target.Id, admin.Id, target.Email!);

        var live = await _ctx.LoginSessions.AsNoTracking()
            .CountAsync(s => s.UserId == target.Id && s.RevokedAt == null);

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.FailureCode, Is.EqualTo("no_change"));
            Assert.That(live, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Handle_UnknownAccountIsNotFound()
    {
        var admin = await SeedAsync(UserType.Admin);

        var response = await SetAsync("user_missing", admin.Id, $"x-{Guid.NewGuid():N}@example.com");

        Assert.That(response.FailureCode, Is.EqualTo("not_found"));
    }
}
