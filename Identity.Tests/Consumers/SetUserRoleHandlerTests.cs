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

/// <summary>Promotion and demotion of staff.</summary>
[TestFixture]
public class SetUserRoleHandlerTests
{
    private static IAlbaHost Host => AppFixture.Host;

    private IServiceScope _scope = null!;
    private MicroserviceContext _ctx = null!;
    private SetUserRoleHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _scope = Host.Services.CreateScope();
        _ctx = _scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        _handler = new SetUserRoleHandler();
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
            Email = $"role-{Guid.NewGuid():N}@example.com",
            PhoneNumber = $"+4179{Random.Shared.Next(1000000, 9999999)}",
            Username = $"rl{Guid.NewGuid():N}"[..15],
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
    private async Task<SetUserRoleResponse> SetAsync(string userId, string actorId, string role)
    {
        var response = await _handler.Handle(
            new SetUserRoleRequest { UserId = userId, ActorUserId = actorId, Role = role },
            _ctx,
            NullLogger<SetUserRoleHandler>.Instance);

        await _ctx.SaveChangesAsync();
        return response;
    }

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_PromotesAnOrdinaryAccountToModerator()
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync();

        var response = await SetAsync(target.Id, admin.Id, "Moderator");

        Assert.Multiple(async () =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.Role, Is.EqualTo("Moderator"));
            Assert.That(await _ctx.Users.Where(u => u.Id == target.Id).Select(u => u.UserType).SingleAsync(),
                Is.EqualTo(UserType.Moderator));
        });
    }

    [Test]
    public async Task Handle_DemotesAModeratorWhenAnotherAdministratorRemains()
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync(UserType.Moderator);

        var response = await SetAsync(target.Id, admin.Id, "Default");

        Assert.That(response.Success, Is.True);
        Assert.That(response.Role, Is.EqualTo("Default"));
    }

    /// <summary>Written against the account whose tier changed, so it lands on that account's own
    /// security timeline rather than only in the gateway's actor-attributed log.</summary>
    [Test]
    public async Task Handle_AuditsTheChangeAgainstTheAffectedAccount()
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync();

        await SetAsync(target.Id, admin.Id, "Admin");

        var audit = await _ctx.IdentityAuditEvents.SingleAsync(e => e.UserId == target.Id);

        Assert.Multiple(() =>
        {
            Assert.That(audit.Action, Is.EqualTo(IdentityAuditActions.StaffRoleChanged));
            Assert.That(audit.Detail, Does.Contain("Default -> Admin"));
            Assert.That(audit.Detail, Does.Contain(admin.Id), "the acting administrator has to be named");
        });
    }

    // ── edge ────────────────────────────────────────────────────────────────

    /// <summary>Two administrators setting the same tier is a race, not an error - the second should
    /// see the state the first produced.</summary>
    [Test]
    public async Task Handle_SettingTheCurrentRoleSucceedsWithoutWriting()
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync(UserType.Moderator);

        var response = await SetAsync(target.Id, admin.Id, "Moderator");

        Assert.Multiple(async () =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.FailureCode, Is.EqualTo("no_change"));
            Assert.That(await _ctx.IdentityAuditEvents.AnyAsync(), Is.False, "no audit row for a no-op");
        });
    }

    [TestCase("moderator")]
    [TestCase("ADMIN")]
    public async Task Handle_AcceptsAnyCasingOfAKnownRole(string role)
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync();

        Assert.That((await SetAsync(target.Id, admin.Id, role)).Success, Is.True);
    }

    /// <summary>Demoting the last administrator is allowed as soon as a second one exists - the
    /// guard is about leaving nobody, not about protecting an individual.</summary>
    [Test]
    public async Task Handle_TheLastAdministratorCanBeDemotedOnceAnotherExists()
    {
        var first = await SeedAsync(UserType.Admin);
        var second = await SeedAsync(UserType.Admin);

        Assert.That((await SetAsync(second.Id, first.Id, "Default")).Success, Is.True);
    }

    // ── negative ────────────────────────────────────────────────────────────

    /// <summary>The guard that matters most.</summary>
    [Test]
    public async Task Handle_RefusesToDemoteTheLastActiveAdministrator()
    {
        var acting = await SeedAsync(UserType.Admin);
        var last = await SeedAsync(UserType.Admin);

        // Take the acting administrator out of the count without deleting them: a banned admin is
        // not somebody who can undo this, so they must not keep the guard open.
        acting.Status = UserStatus.Banned;
        await _ctx.SaveChangesAsync();

        var response = await SetAsync(last.Id, acting.Id, "Moderator");

        Assert.Multiple(async () =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.FailureCode, Is.EqualTo("last_administrator"));
            Assert.That(await _ctx.Users.Where(u => u.Id == last.Id).Select(u => u.UserType).SingleAsync(),
                Is.EqualTo(UserType.Admin), "the row must be untouched");
        });
    }

    /// <summary>Never intentional, and it is how an instance's only administrator locks themselves
    /// out of their own console.</summary>
    [Test]
    public async Task Handle_RefusesToChangeYourOwnRole()
    {
        var admin = await SeedAsync(UserType.Admin);

        var response = await SetAsync(admin.Id, admin.Id, "Default");

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.FailureCode, Is.EqualTo("self_action"));
        });
    }

    /// <summary>Bot-ness is a property of how the account was created, not a role to be assigned:
    /// promoting a human to Bot would produce an account the rest of the system treats as
    /// machine-operated.</summary>
    [Test]
    public async Task Handle_RefusesToAssignTheBotType()
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync();

        var response = await SetAsync(target.Id, admin.Id, "Bot");

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.FailureCode, Is.EqualTo("invalid_role"));
        });
    }

    [Test]
    public async Task Handle_RefusesToGiveABotAccountAStaffRole()
    {
        var admin = await SeedAsync(UserType.Admin);
        var bot = await SeedAsync(UserType.Bot);

        var response = await SetAsync(bot.Id, admin.Id, "Moderator");

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.FailureCode, Is.EqualTo("bot_account"));
        });
    }

    /// <summary>A banned account handed the console is somebody who cannot sign in - and un-banning
    /// them later would silently make them staff.</summary>
    [Test]
    public async Task Handle_RefusesToPromoteAnAccountThatIsNotActive()
    {
        var admin = await SeedAsync(UserType.Admin);
        var banned = await SeedAsync(status: UserStatus.Banned);

        var response = await SetAsync(banned.Id, admin.Id, "Moderator");

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.FailureCode, Is.EqualTo("account_inactive"));
        });
    }

    /// <summary>Demotion to Default stays available for an inactive account - stripping staff from
    /// someone who has just been banned must not be blocked by the fact that they were banned.</summary>
    [Test]
    public async Task Handle_AllowsDemotingAnAccountThatIsNotActive()
    {
        var admin = await SeedAsync(UserType.Admin);
        var banned = await SeedAsync(UserType.Moderator, UserStatus.Banned);

        Assert.That((await SetAsync(banned.Id, admin.Id, "Default")).Success, Is.True);
    }

    [Test]
    public async Task Handle_RefusesAnUnknownRole()
    {
        var admin = await SeedAsync(UserType.Admin);
        var target = await SeedAsync();

        var response = await SetAsync(target.Id, admin.Id, "Superuser");

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.FailureCode, Is.EqualTo("invalid_role"));
        });
    }

    [Test]
    public async Task Handle_RefusesAnUnknownAccount()
    {
        var admin = await SeedAsync(UserType.Admin);

        var response = await SetAsync("user_doesnotexist", admin.Id, "Moderator");

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.FailureCode, Is.EqualTo("not_found"));
        });
    }
}
