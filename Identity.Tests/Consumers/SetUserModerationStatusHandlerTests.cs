using Alba;
using Identity.Application.Consumers;
using Identity.Contracts.Bus.Events;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Aggregates;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.Tracking;

namespace Identity.Tests.Consumers;

/// <summary>
/// The ban event this handler cascades, and every path that must not cascade one.
/// </summary>
[TestFixture]
public class SetUserModerationStatusHandlerTests
{
    private static IAlbaHost Host => AppFixture.Host;

    private IServiceScope _scope = null!;
    private MicroserviceContext _ctx = null!;
    private SetUserModerationStatusHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _scope = Host.Services.CreateScope();
        _ctx = _scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        _handler = new SetUserModerationStatusHandler();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (await _ctx.IdentityAuditEvents.AnyAsync())
        {
            _ctx.IdentityAuditEvents.RemoveRange(_ctx.IdentityAuditEvents);
            await _ctx.SaveChangesAsync();
        }

        if (await _ctx.Users.AnyAsync())
        {
            _ctx.Users.RemoveRange(_ctx.Users);
            await _ctx.SaveChangesAsync();
        }

        _scope.Dispose();
    }

    private async Task<ApplicationUser> SeedAsync(
        UserType type = UserType.Default, UserStatus status = UserStatus.Active)
    {
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = $"mod-{Guid.NewGuid():N}@example.com",
            PhoneNumber = $"+4179{Random.Shared.Next(1000000, 9999999)}",
            Username = $"md{Guid.NewGuid():N}"[..15],
            BirthDate = new DateOnly(1990, 1, 1),
        });

        user.UserType = type;
        user.Status = status;

        _ctx.Users.Add(user);
        await _ctx.SaveChangesAsync();
        return user;
    }

    /// <summary>The handler relies on Wolverine's middleware to commit, so the tests do what the
    /// middleware would.</summary>
    private async Task<(SetUserModerationStatusResponse Response, UserModerationStatusChangedEvent? Event)>
        SetAsync(string userId, string actorId, bool banned, string? reason = "Ban: chargeback fraud")
    {
        var result = await _handler.Handle(
            new SetUserModerationStatusRequest
            {
                UserId = userId,
                ActorUserId = actorId,
                Banned = banned,
                Reason = reason,
            },
            _ctx,
            NullLogger<SetUserModerationStatusHandler>.Instance);

        await _ctx.SaveChangesAsync();
        return result;
    }

    // ── normal ──────────────────────────────────────────────────────────────

    /// <summary>Everything a consumer needs to act without asking Identity a second question. The
    /// reason travels because the fraud void writes it onto the reversal entries, which is the only
    /// place a voided balance ever explains itself.</summary>
    [Test]
    public async Task Handle_ABanCascadesTheEventWithTheWholeTransition()
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync();

        var before = DateTimeOffset.UtcNow;
        var (response, cascaded) = await SetAsync(target.Id, admin.Id, banned: true);

        Assert.That(cascaded, Is.Not.Null, "a ban that took effect has to be announced");

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(cascaded!.UserId, Is.EqualTo(target.Id));
            Assert.That(cascaded.Banned, Is.True);
            Assert.That(cascaded.Status, Is.EqualTo(nameof(UserStatus.Banned)));
            Assert.That(cascaded.PreviousStatus, Is.EqualTo(nameof(UserStatus.Active)));
            Assert.That(cascaded.ActorUserId, Is.EqualTo(admin.Id));
            Assert.That(cascaded.Reason, Is.EqualTo("Ban: chargeback fraud"));
            Assert.That(cascaded.OccurredAt, Is.GreaterThanOrEqualTo(before));
        });
    }

    /// <summary>One event, not one per returned member.</summary>
    [Test]
    public async Task Handle_ABanCascadesTheEventAlongsideTheRowItChanged()
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync();

        var (_, cascaded) = await SetAsync(target.Id, admin.Id, banned: true);

        Assert.Multiple(async () =>
        {
            Assert.That(cascaded, Is.Not.Null);
            Assert.That(await _ctx.Users.Where(u => u.Id == target.Id).Select(u => u.Status).SingleAsync(),
                Is.EqualTo(UserStatus.Banned), "the announcement and the row have to agree");
            Assert.That(await _ctx.IdentityAuditEvents.CountAsync(e => e.UserId == target.Id), Is.EqualTo(1));
        });
    }

    /// <summary>
    /// Through the bus, the way the console actually calls it: the caller still gets its response
    /// and the event goes out anyway.
    /// </summary>
    [Test]
    public async Task InvokeAsync_ReturnsTheResponseAndStillSendsTheCascadedEvent()
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync();

        SetUserModerationStatusResponse response = null!;

        var tracked = await Host.ExecuteAndWaitAsync(async () =>
        {
            var bus = _scope.ServiceProvider.GetRequiredService<IMessageBus>();

            response = await bus.InvokeAsync<SetUserModerationStatusResponse>(
                new SetUserModerationStatusRequest
                {
                    UserId = target.Id,
                    ActorUserId = admin.Id,
                    Banned = true,
                    Reason = "Ban: chargeback fraud",
                });
        });

        var cascaded = tracked.FindSingleTrackedMessageOfType<UserModerationStatusChangedEvent>();

        Assert.Multiple(async () =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.Status, Is.EqualTo(nameof(UserStatus.Banned)));
            Assert.That(cascaded.UserId, Is.EqualTo(target.Id));
            Assert.That(cascaded.Banned, Is.True);
            Assert.That(await _ctx.Users.Where(u => u.Id == target.Id).Select(u => u.Status).SingleAsync(),
                Is.EqualTo(UserStatus.Banned), "and the ban still took effect");
        });
    }

    // ── edge ────────────────────────────────────────────────────────────────

    /// <summary>The restore half travels too.</summary>
    [Test]
    public async Task Handle_AnUnbanCascadesTheEventCarryingTheRestoredStatus()
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync(status: UserStatus.Banned);

        var (response, cascaded) = await SetAsync(target.Id, admin.Id, banned: false, reason: "Unban: appeal upheld");

        Assert.That(cascaded, Is.Not.Null);

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(cascaded!.Banned, Is.False);
            Assert.That(cascaded.Status, Is.EqualTo(nameof(UserStatus.Active)));
            Assert.That(cascaded.PreviousStatus, Is.EqualTo(nameof(UserStatus.Banned)));
            Assert.That(cascaded.Reason, Is.EqualTo("Unban: appeal upheld"));
        });
    }

    /// <summary>A missing reason must not stop the announcement.</summary>
    [Test]
    public async Task Handle_ABanWithNoReasonStillCascades()
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync();

        var (_, cascaded) = await SetAsync(target.Id, admin.Id, banned: true, reason: null);

        Assert.That(cascaded, Is.Not.Null);
        Assert.That(cascaded!.Reason, Is.Null);
    }

    // ── negative: the five paths that changed nothing ───────────────────────

    /// <summary>Refused before the row is even loaded, so there is nothing to announce.</summary>
    [Test]
    public async Task Handle_SelfActionCascadesNothing()
    {
        var admin = await SeedAsync(UserType.Admin);

        var (response, cascaded) = await SetAsync(admin.Id, admin.Id, banned: true);

        Assert.Multiple(() =>
        {
            Assert.That(response.FailureCode, Is.EqualTo("self_action"));
            Assert.That(cascaded, Is.Null);
        });
    }

    /// <summary>An event naming an account that does not exist would send every consumer looking for
    /// state that was never there.</summary>
    [Test]
    public async Task Handle_AnUnknownAccountCascadesNothing()
    {
        var admin = await SeedAsync(UserType.Admin);

        var (response, cascaded) = await SetAsync("user_doesnotexist", admin.Id, banned: true);

        Assert.Multiple(() =>
        {
            Assert.That(response.FailureCode, Is.EqualTo("not_found"));
            Assert.That(cascaded, Is.Null);
        });
    }

    /// <summary>The administrator is not banned, so nothing downstream may behave as though they
    /// were - voiding an admin's wallet on a refused ban would be the worst version of this bug.
    /// </summary>
    [Test]
    public async Task Handle_AProtectedAccountCascadesNothing()
    {
        var acting = await SeedAsync(UserType.Admin);
        var protectedAdmin = await SeedAsync(UserType.Admin);

        var (response, cascaded) = await SetAsync(protectedAdmin.Id, acting.Id, banned: true);

        Assert.Multiple(() =>
        {
            Assert.That(response.FailureCode, Is.EqualTo("protected_account"));
            Assert.That(cascaded, Is.Null);
        });
    }

    /// <summary>The dangerous one, because it succeeds.</summary>
    [Test]
    public async Task Handle_ARepeatedBanSucceedsAndCascadesNothing()
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync(status: UserStatus.Banned);

        var (response, cascaded) = await SetAsync(target.Id, admin.Id, banned: true);

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True, "the second moderator sees the state, not a failure");
            Assert.That(response.FailureCode, Is.EqualTo("no_change"));
            Assert.That(cascaded, Is.Null);
        });
    }

    [TestCase(UserStatus.PendingDeletion)]
    [TestCase(UserStatus.PurgeInProgress)]
    [TestCase(UserStatus.Deleted)]
    public async Task Handle_AnAccountMidwayThroughErasureCascadesNothing(UserStatus status)
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync(status: status);

        var (response, cascaded) = await SetAsync(target.Id, admin.Id, banned: true);

        Assert.Multiple(() =>
        {
            Assert.That(response.FailureCode, Is.EqualTo("invalid_state"));
            Assert.That(cascaded, Is.Null);
        });
    }
}
