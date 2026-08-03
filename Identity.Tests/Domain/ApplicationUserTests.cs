using FluentValidation;
using Identity.Domain.Aggregates;
using Identity.Domain.Enums;
using Identity.Domain.Events.User;

namespace Identity.Tests.Domain;

/// <summary>Pure-logic coverage for ApplicationUser's state machine and factory methods - none of
/// these need EF Core or UserManager, so they're tested directly against the aggregate.</summary>
[TestFixture]
public class ApplicationUserTests
{
    private static CreateUserParams ValidParams(string? email = null, string? username = null) => new()
    {
        Email = email ?? $"user-{Guid.NewGuid():N}@example.com",
        Username = username ?? $"user{Guid.NewGuid():N}"[..12],
        PhoneNumber = null!,
        BirthDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-20)),
    };

    // ── Create ──────────────────────────────────────────────────────────────

    [Test]
    public void Create_ValidParams_SetsCoreFieldsAndActiveStatus()
    {
        var email = $"racer-{Guid.NewGuid():N}@example.com";
        var username = $"racer{Guid.NewGuid():N}"[..10];

        var user = ApplicationUser.Create(ValidParams(email, username));

        Assert.That(user.Email, Is.EqualTo(email));
        Assert.That(user.NormalizedEmail, Is.EqualTo(email.ToUpperInvariant()));
        Assert.That(user.UserName, Is.EqualTo(username));
        Assert.That(user.NormalizedUserName, Is.EqualTo(username.ToUpperInvariant()));
        Assert.That(user.Status, Is.EqualTo(UserStatus.Active));
        Assert.That(user.UserType, Is.EqualTo(UserType.Default));
        Assert.That(user.Id, Does.StartWith("user_"));
        Assert.That(user.CorrelationId, Is.EqualTo(user.Id));
    }

    [Test]
    public void Create_EnablesLockout()
    {
        // Users are persisted with ctx.Users.Add, never UserManager.CreateAsync - which is the only
        // place ASP.NET Identity would set this. With it false, UserManager.IsLockedOutAsync
        // short-circuits to false and SignInManager can never return LockedOut, so every
        // lockout-aware password gate in this service is silently inert while access_failed_count
        // and lockout_end are still being written.
        var user = ApplicationUser.Create(ValidParams());

        Assert.That(user.LockoutEnabled, Is.True);
    }

    [Test]
    public void CreateBot_EnablesLockout()
    {
        var bot = ApplicationUser.CreateBot("user_bot1", "Test Bot");

        Assert.That(bot.LockoutEnabled, Is.True);
    }

    [Test]
    public void Create_ValidParams_AddsUserCreatedDomainEventWithMatchingCorrelationId()
    {
        var user = ApplicationUser.Create(ValidParams());

        var events = user.GetDomainEvents();
        Assert.That(events, Has.Count.EqualTo(1));
        var created = events.Single() as UserCreated;
        Assert.That(created, Is.Not.Null);
        Assert.That(created!.UserId, Is.EqualTo(user.Id));
        Assert.That(created.Email, Is.EqualTo(user.Email));
        Assert.That(created.CorrelationId, Is.EqualTo(user.CorrelationId));
    }

    [Test]
    public void Create_InvalidEmail_ThrowsValidationException()
    {
        var invalidParams = ValidParams(email: "not-an-email");

        Assert.Throws<ValidationException>(() => ApplicationUser.Create(invalidParams));
    }

    [Test]
    public void Create_UnderageBirthDate_ThrowsValidationException()
    {
        var underageParams = ValidParams();
        underageParams = new CreateUserParams
        {
            Email = underageParams.Email,
            Username = underageParams.Username,
            PhoneNumber = underageParams.PhoneNumber,
            BirthDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-10)),
        };

        Assert.Throws<ValidationException>(() => ApplicationUser.Create(underageParams));
    }

    // ── CreateBot ───────────────────────────────────────────────────────────

    [Test]
    public void CreateBot_UsesCallerSuppliedIdAndSkipsWelcomeMachinery()
    {
        const string botId = "bot_abc123";

        var bot = ApplicationUser.CreateBot(botId, "MyBot");

        Assert.That(bot.Id, Is.EqualTo(botId));
        Assert.That(bot.CorrelationId, Is.EqualTo(botId));
        Assert.That(bot.UserType, Is.EqualTo(UserType.Bot));
        Assert.That(bot.Status, Is.EqualTo(UserStatus.Active));
        Assert.That(bot.NormalizedUserName, Is.EqualTo("MYBOT"));
        Assert.That(bot.GetDomainEvents(), Is.Empty, "Bot creation should not publish UserCreated (no welcome email/profile stitching needed)");
    }

    // ── IsSigninAllowed ─────────────────────────────────────────────────────

    [TestCase(UserStatus.Active, true)]
    [TestCase(UserStatus.Inactive, false)]
    [TestCase(UserStatus.Banned, false)]
    [TestCase(UserStatus.PendingDeletion, false)]
    [TestCase(UserStatus.PurgeInProgress, false)]
    [TestCase(UserStatus.Deleted, false)]
    public void IsSigninAllowed_ReturnsTrueOnlyWhenActive(UserStatus status, bool expected)
    {
        var user = ApplicationUser.Create(ValidParams());
        user.Status = status;

        Assert.That(user.IsSigninAllowed(), Is.EqualTo(expected));
    }

    // ── RequestDeletion / CancelDeletionRequest ─────────────────────────────

    [Test]
    public void RequestDeletion_StartsGracePeriodCountdown()
    {
        var user = ApplicationUser.Create(ValidParams());
        var scheduledAt = DateTimeOffset.UtcNow.AddDays(30);

        user.RequestDeletion(scheduledAt);

        Assert.That(user.Status, Is.EqualTo(UserStatus.PendingDeletion));
        Assert.That(user.PurgeScheduledAt, Is.EqualTo(scheduledAt));
        Assert.That(user.DeletionRequestedAt, Is.Not.Null);
        Assert.That(user.IsSigninAllowed(), Is.False, "Requesting deletion should immediately block sign-in");
    }

    [Test]
    public void CancelDeletionRequest_WhenPending_RevertsToActiveAndReturnsTrue()
    {
        var user = ApplicationUser.Create(ValidParams());
        user.RequestDeletion(DateTimeOffset.UtcNow.AddDays(30));

        var result = user.CancelDeletionRequest();

        Assert.That(result, Is.True);
        Assert.That(user.Status, Is.EqualTo(UserStatus.Active));
        Assert.That(user.DeletionRequestedAt, Is.Null);
        Assert.That(user.PurgeScheduledAt, Is.Null);
    }

    [Test]
    public void CancelDeletionRequest_WhenNotPending_NoOpsAndReturnsFalse()
    {
        var user = ApplicationUser.Create(ValidParams());
        // Status is Active, never requested deletion.

        var result = user.CancelDeletionRequest();

        Assert.That(result, Is.False);
        Assert.That(user.Status, Is.EqualTo(UserStatus.Active));
    }

    [Test]
    public void CancelDeletionRequest_AfterPurgeStarted_CannotUnwind()
    {
        var user = ApplicationUser.Create(ValidParams());
        user.RequestDeletion(DateTimeOffset.UtcNow.AddDays(30));
        user.BeginPurge();

        var result = user.CancelDeletionRequest();

        Assert.That(result, Is.False, "Once the fan-out has started, cancellation must not be possible");
        Assert.That(user.Status, Is.EqualTo(UserStatus.PurgeInProgress));
    }

    // ── BeginPurge ──────────────────────────────────────────────────────────

    [Test]
    public void BeginPurge_MarksAccountAsNoLongerCancellable()
    {
        var user = ApplicationUser.Create(ValidParams());
        user.RequestDeletion(DateTimeOffset.UtcNow.AddDays(30));

        user.BeginPurge();

        Assert.That(user.Status, Is.EqualTo(UserStatus.PurgeInProgress));
    }

    // ── Tombstone ───────────────────────────────────────────────────────────

    [Test]
    public void Tombstone_ScrubsPersonalDataAndSetsDeletedStatus()
    {
        var user = ApplicationUser.Create(ValidParams());
        user.SteamId = "76561198000000000";
        user.PasswordHash = "some-hash";
        user.JsonSettings = "{\"theme\":\"dark\"}";

        user.Tombstone();

        Assert.That(user.Status, Is.EqualTo(UserStatus.Deleted));
        Assert.That(user.Email, Is.Null);
        Assert.That(user.NormalizedEmail, Is.Null);
        Assert.That(user.PhoneNumber, Is.Null);
        Assert.That(user.Bio, Is.Null);
        Assert.That(user.PasswordHash, Is.Null);
        Assert.That(user.SteamId, Is.Null);
        Assert.That(user.JsonSettings, Is.EqualTo("{}"));
        Assert.That(user.UserName, Does.StartWith("Deleted User "));
        Assert.That(user.IsSigninAllowed(), Is.False);
    }

    [Test]
    public void Tombstone_UsernameSuffix_IsLastSixCharsOfId()
    {
        var user = ApplicationUser.Create(ValidParams());
        var expectedSuffix = user.Id[^6..];

        user.Tombstone();

        Assert.That(user.UserName, Is.EqualTo($"Deleted User {expectedSuffix}"));
        Assert.That(user.NormalizedUserName, Is.EqualTo(user.UserName!.ToUpperInvariant()));
    }

    [Test]
    public void Tombstone_CalledTwice_IsIdempotent()
    {
        var user = ApplicationUser.Create(ValidParams());
        user.Tombstone();
        var userNameAfterFirst = user.UserName;
        var stampAfterFirst = user.SecurityStamp;

        user.Tombstone();

        Assert.That(user.UserName, Is.EqualTo(userNameAfterFirst),
            "A redelivered PurgeUserDataCommand must not re-scrub (and re-randomize) an already-tombstoned account");
        Assert.That(user.SecurityStamp, Is.EqualTo(stampAfterFirst));
    }

    // ── AddDomainEvent / GetDomainEvents ────────────────────────────────────

    [Test]
    public void AddDomainEvent_StampsEventWithUsersCorrelationId()
    {
        var user = ApplicationUser.CreateBot("bot_xyz", "Bot");
        var evt = new UserCreated { UserId = user.Id, Email = "x@example.com", UserName = "x" };

        user.AddDomainEvent(evt);

        Assert.That(evt.CorrelationId, Is.EqualTo(user.CorrelationId));
        Assert.That(user.GetDomainEvents(), Has.Count.EqualTo(1));
    }
}
