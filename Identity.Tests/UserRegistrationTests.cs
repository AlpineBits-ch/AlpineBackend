using System.Net;
using System.Text.Json;
using Alba;
using Identity.Application.Dtos.Request;
using Identity.Domain.Events.User;
using Identity.Infrastructure.Persistence;
using Identity.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Tracking;

namespace Identity.Tests;

/// <summary>
/// Integration tests for the user sign-up flow.
///
/// Coverage:
///   - POST /api/v1/authentication/register happy path (now 202, with no user id in the body)
///   - User is persisted to the database with the correct fields
///   - UserCreatedEvent is published, which the Social service consumes to create the Profile
///   - A taken address is answered byte-for-byte as a free one, and creates nothing
///   - A taken username is still refused, and that refusal does not depend on the address
///   - Underage-user rejection (&lt; 13 years old per AgeValidator), also independent of the address
/// </summary>
[TestFixture]
public class UserRegistrationTests
{
    private const string Password = "SecurePass123!";

    private static IAlbaHost Host => AppFixture.Host;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<(ITrackedSession Tracked, IScenarioResult Result)> TrackedHttpCall(
        Action<Scenario> configure, int timeoutMs = 15_000)
    {
        IScenarioResult result = null!;
        var tracked = await Host.ExecuteAndWaitAsync(async () =>
        {
            result = await Host.Scenario(configure);
        }, timeoutMs);
        return (tracked, result);
    }

    /// <summary>A username nothing else in the suite can collide with. Registration refuses a taken
    /// one, so a fixed literal shared between two calls would fail for the wrong reason.</summary>
    private static string NewUsername(string prefix = "reg") => $"{prefix}{Guid.NewGuid():N}"[..15];

    private static CreateUserRequest ValidRequest(string? email = null, string? username = null) => new()
    {
        Email    = email ?? $"user-{Guid.NewGuid()}@example.com",
        Password = Password,
        Username = username ?? NewUsername(),
        BirthDate = DateTime.UtcNow.AddYears(-20),
    };

    /// <summary>Posts a registration and returns everything an anonymous caller can observe about
    /// the answer - see <see cref="ResponseFingerprint"/>. Comparing two of these is the test.</summary>
    private static Task<string> FingerprintOfAsync(CreateUserRequest request) =>
        Host.Scenario(x =>
        {
            x.Post.Json(request).ToUrl("/api/v1/authentication/register");
            x.IgnoreStatusCode();
        }).ContinueWith(t => ResponseFingerprint.OfAsync(t.Result)).Unwrap();

    private static async Task RegisterAsync(CreateUserRequest request) =>
        await Host.Scenario(x =>
        {
            x.Post.Json(request).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

    // ── Happy path ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Register_ValidRequest_Returns202WithTheUniformBodyAndNoUserId()
    {
        var (_, result) = await TrackedHttpCall(x =>
        {
            x.Post.Json(ValidRequest()).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();

        Assert.Multiple(() =>
        {
            Assert.That(body.TryGetProperty("userId", out _), Is.False,
                "the id of the new account is the success signal that made this endpoint an oracle - "
                + "it cannot be in a response that also has to cover the already-registered branch");
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("verification_pending"));
            Assert.That(body.GetProperty("message").GetString(), Is.Not.Empty);
        });
    }

    [Test]
    public async Task Register_ValidRequest_UserPersistedToDatabase()
    {
        var email = $"persist-{Guid.NewGuid()}@example.com";

        await TrackedHttpCall(x =>
        {
            x.Post.Json(ValidRequest(email)).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        using var scope = Host.Services.CreateScope();
        var ctx  = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Email == email);

        Assert.That(user, Is.Not.Null, "User should be saved in the database");
        Assert.That(user!.Email, Is.EqualTo(email));
        Assert.That(user.EmailConfirmed, Is.True, "Email should be confirmed when verification is disabled");
    }

    // ── Profile stitching ──────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that registering a user publishes the UserCreatedEvent contract that the
    /// Social service subscribes to in order to create a Profile.  The event must carry
    /// the username so the Profile can be initialised correctly.
    /// </summary>
    [Test]
    public async Task Register_ValidRequest_PublishesUserCreatedEventForProfileStitching()
    {
        var username = NewUsername("profile");

        var (tracked, _) = await TrackedHttpCall(x =>
        {
            x.Post.Json(ValidRequest(username: username)).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        // UserCreatedEvent is the integration contract that Social.Application
        // consumes to create the user's Profile aggregate.
        var evt = tracked.FindSingleTrackedMessageOfType<UserCreated>();
        Assert.That(evt, Is.Not.Null, "UserCreatedEvent should be published after registration");
        Assert.That(evt!.UserName, Is.EqualTo(username));
        Assert.That(evt.UserId, Is.Not.Null.And.Not.Empty);
    }

    // ── Account enumeration ────────────────────────────────────────────────────
    //
    // This endpoint is anonymous and used to answer 400 "Email already exists" for a taken address
    // and 200 {userId} for a free one, which turned an arbitrary address list into a membership
    // census one POST at a time - and walked around the discoverability controls in
    // docs/specs/privacy.md (T2-16), which go to some trouble to make "not discoverable" and "does
    // not exist" indistinguishable to callers who at least have an account of their own.

    [Test]
    public async Task Register_KnownAndUnknownAddress_AreAnsweredIdentically()
    {
        var taken = $"taken-{Guid.NewGuid()}@example.com";
        await RegisterAsync(ValidRequest(taken));

        // Fresh usernames on both probes: the username refusal is deliberately kept (see below), so
        // reusing the first account's name would compare two username refusals instead.
        var known = await FingerprintOfAsync(ValidRequest(taken, NewUsername()));
        var unknown = await FingerprintOfAsync(ValidRequest($"free-{Guid.NewGuid()}@example.com", NewUsername()));

        Assert.Multiple(() =>
        {
            Assert.That(known, Is.EqualTo(unknown),
                "status, observable headers and body must all match - this used to be 400 \"Email "
                + "already exists\" against 200 with a user id");
            Assert.That(unknown, Does.Contain("status: 202"));
        });
    }

    [Test]
    public async Task Register_ExistingAddress_CreatesNothingAndLeavesTheAccountAlone()
    {
        var email = $"nodupe-{Guid.NewGuid()}@example.com";
        await RegisterAsync(ValidRequest(email));

        string beforeHash;
        int consentsBefore;
        string userId;

        using (var scope = Host.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
            var user = await ctx.Users.SingleAsync(u => u.Email == email);
            userId = user.Id;
            beforeHash = user.PasswordHash!;
            consentsBefore = await ctx.UserConsents.CountAsync(c => c.UserId == userId);
        }

        var intruderUsername = NewUsername("intruder");
        await RegisterAsync(new CreateUserRequest
        {
            Email = email,
            Password = "SomeoneElsesPassword123!",
            Username = intruderUsername,
            BirthDate = DateTime.UtcNow.AddYears(-40),
        });

        using var after = Host.Services.CreateScope();
        var afterCtx = after.ServiceProvider.GetRequiredService<MicroserviceContext>();

        Assert.Multiple(() =>
        {
            Assert.That(afterCtx.Users.Count(u => u.Email == email), Is.EqualTo(1),
                "the accepted-looking answer must not have created a second account");
            Assert.That(afterCtx.Users.Any(u => u.UserName == intruderUsername), Is.False,
                "nothing the second caller sent may be persisted anywhere");
            Assert.That(afterCtx.Users.Single(u => u.Email == email).PasswordHash, Is.EqualTo(beforeHash),
                "an anonymous caller must not be able to touch an existing account's credentials");
            Assert.That(afterCtx.UserConsents.Count(c => c.UserId == userId), Is.EqualTo(consentsBefore),
                "T1-10: a consent row stamped with a stranger's IP against an account they do not "
                + "own would be a fabricated legal record, not a missing one");
        });
    }

    [Test]
    public async Task Register_ExistingAddress_PublishesNoUserCreatedEvent()
    {
        var email = $"noevent-{Guid.NewGuid()}@example.com";
        await RegisterAsync(ValidRequest(email));

        var (tracked, _) = await TrackedHttpCall(x =>
        {
            x.Post.Json(ValidRequest(email, NewUsername())).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        Assert.That(tracked.Executed.MessagesOf<UserCreated>(), Is.Empty,
            "Social would materialize a second profile for an account that was never created");
    }

    // ── Username collisions are deliberately still distinguishable ─────────────
    //
    // Usernames are discoverable by design here (DiscoverableByUsername defaults true and username
    // lookup is how friend requests are addressed), so "that name is taken" reveals nothing an
    // account holder cannot already establish - and the user cannot pick a different one without
    // being told. The rule that keeps it safe is ordering: the username is checked before the
    // address is looked up, so the refusal is a function of the username alone.

    [Test]
    public async Task Register_TakenUsername_IsStillRefusedWithAUsableMessage()
    {
        var username = NewUsername("taken");
        await RegisterAsync(ValidRequest(username: username));

        var result = await Host.Scenario(x =>
        {
            x.Post.Json(ValidRequest(username: username)).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });

        var body = await result.ReadAsTextAsync();
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("username").IgnoreCase);
            // The previous fix for this endpoint stopped a duplicate username echoing raw Postgres
            // constraint text to anonymous callers. It must not come back via the explicit check.
            Assert.That(body, Does.Not.Contain("normalized").IgnoreCase);
            Assert.That(body, Does.Not.Contain("ix_").IgnoreCase);
            Assert.That(body, Does.Not.Contain("duplicate key").IgnoreCase);
        });
    }

    [Test]
    public async Task Register_TakenUsername_IsRefusedTheSameWayForKnownAndUnknownAddresses()
    {
        var username = NewUsername("shared");
        var takenEmail = $"both-{Guid.NewGuid()}@example.com";
        await RegisterAsync(ValidRequest(takenEmail, username));

        var withKnownAddress = await FingerprintOfAsync(ValidRequest(takenEmail, username));
        var withUnknownAddress = await FingerprintOfAsync(
            ValidRequest($"free-{Guid.NewGuid()}@example.com", username));

        Assert.That(withKnownAddress, Is.EqualTo(withUnknownAddress),
            "the kept username refusal must not become a side door onto the address: checked after "
            + "the address lookup, a taken username would answer 400 for a free address and 202 for "
            + "a registered one");
    }

    // ── Validation / rejection ─────────────────────────────────────────────────

    [Test]
    public async Task Register_UnderageBirthDate_Returns400()
    {
        // AgeValidator requires birth date < 13 years ago (LessThan rule).
        // Using a 10-year-old should be rejected.
        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email     = $"underage-{Guid.NewGuid()}@example.com",
                Password  = Password,
                Username  = NewUsername("young"),
                BirthDate = DateTime.UtcNow.AddYears(-10),
            }).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Test]
    public async Task Register_UnderageBirthDate_IsRefusedTheSameWayForKnownAndUnknownAddresses()
    {
        var email = $"age-{Guid.NewGuid()}@example.com";
        await RegisterAsync(ValidRequest(email));

        CreateUserRequest Underage(string address) => new()
        {
            Email = address,
            Password = Password,
            Username = NewUsername("young"),
            BirthDate = DateTime.UtcNow.AddYears(-10),
        };

        var known = await FingerprintOfAsync(Underage(email));
        var unknown = await FingerprintOfAsync(Underage($"age-{Guid.NewGuid()}@example.com"));

        Assert.Multiple(() =>
        {
            Assert.That(known, Is.EqualTo(unknown),
                "the age floor is checked before the address is looked up on purpose - checked after, "
                + "an underage signup would 400 for a free address and 202 for a registered one, "
                + "which is the oracle again in the last shape anyone would look for");
            Assert.That(known, Does.Contain("status: 400"));
        });
    }

    [Test]
    public async Task Register_MissingEmail_IsStillRefused()
    {
        // Input validation survives the uniform 202: whether the caller sent an address at all does
        // not depend on whose address it is.
        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email = "",
                Password = Password,
                Username = NewUsername(),
                BirthDate = DateTime.UtcNow.AddYears(-20),
            }).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [TearDown]
    public async Task ClearDatabaseAfterTest()
    {
        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        if (await ctx.Users.AnyAsync())
        {
            ctx.Users.RemoveRange(ctx.Users);
            await ctx.SaveChangesAsync();
        }
    }
}
