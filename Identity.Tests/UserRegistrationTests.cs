using System.Net;
using Alba;
using Identity.Application.Dtos.Request;
using Identity.Contracts.Bus.Events;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Aggregates;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Tracking;

namespace Identity.Tests;

/// <summary>
/// Integration tests for the user sign-up flow.
///
/// Coverage:
///   - POST /api/v1/authentication/register happy path
///   - User is persisted to the database with the correct fields
///   - UserCreatedEvent is published, which the Social service consumes to create the Profile
///   - Duplicate-email rejection
///   - Underage-user rejection (< 13 years old per AgeValidator)
/// </summary>
[TestFixture]
public class UserRegistrationTests
{
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

    private static CreateUserRequest ValidRequest(string? email = null) => new()
    {
        Email    = email ?? $"user-{Guid.NewGuid()}@example.com",
        Password = "SecurePass123!",
        Username = "testuser",
        BirthDate = DateTime.UtcNow.AddYears(-20),
    };

    // ── Happy path ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Register_ValidRequest_Returns200WithUserId()
    {
        var (_, result) = await TrackedHttpCall(x =>
        {
            x.Post.Json(ValidRequest()).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<CreateUserWithEmailAndPasswordResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.UserId, Is.Not.Null.And.Not.Empty);
        Assert.That(body.Failures, Is.Empty);
    }

    [Test]
    public async Task Register_ValidRequest_UserPersistedToDatabase()
    {
        var email = $"persist-{Guid.NewGuid()}@example.com";

        await TrackedHttpCall(x =>
        {
            x.Post.Json(ValidRequest(email)).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
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
    /// Verifies that registering a user publishes the UserCreatedEvent contract that the Social
    /// service subscribes to in order to create a Profile.
    /// </summary>
    [Test]
    public async Task Register_ValidRequest_PublishesUserCreatedEventForProfileStitching()
    {
        const string username = "profileuser";

        var (tracked, _) = await TrackedHttpCall(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email     = $"profile-{Guid.NewGuid()}@example.com",
                Password  = "SecurePass123!",
                Username  = username,
                BirthDate = DateTime.UtcNow.AddYears(-22),
            }).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        // UserCreatedEvent is the integration contract that Social.Application
        // consumes to create the user's Profile aggregate.
        var evt = tracked.FindSingleTrackedMessageOfType<UserCreatedEvent>();
        Assert.That(evt, Is.Not.Null, "UserCreatedEvent should be published after registration");
        Assert.That(evt!.UserName, Is.EqualTo(username));
        Assert.That(evt.UserId, Is.Not.Null.And.Not.Empty);
    }

    // ── Validation / rejection ─────────────────────────────────────────────────

    [Test]
    public async Task Register_DuplicateEmail_Returns400WithValidationFailure()
    {
        var email   = $"dup-{Guid.NewGuid()}@example.com";
        var request = ValidRequest(email);

        // First registration succeeds
        await TrackedHttpCall(x =>
        {
            x.Post.Json(request).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        // Second registration with the same e-mail must be rejected
        var (_, result) = await TrackedHttpCall(x =>
        {
            x.Post.Json(request).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });

        var failures = await result.ReadAsJsonAsync<IEnumerable<object>>();
        Assert.That(failures, Is.Not.Empty, "Response should contain at least one validation failure");
    }

    [Test]
    public async Task Register_UnderageBirthDate_Returns400()
    {
        // AgeValidator requires birth date < 13 years ago (LessThan rule).
        // Using a 10-year-old should be rejected.
        var (_, _) = await TrackedHttpCall(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email     = $"underage-{Guid.NewGuid()}@example.com",
                Password  = "SecurePass123!",
                Username  = "younguser",
                BirthDate = DateTime.UtcNow.AddYears(-10),
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
