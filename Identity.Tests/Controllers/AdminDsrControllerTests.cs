using System.Net;
using System.Text.Json;
using Alba;
using Identity.Application.Dtos.Request;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests.Controllers;

/// <summary><c>api/v1/admin/dsr</c> over real HTTP, real auth and real Postgres (T1-13).</summary>
[TestFixture]
public class AdminDsrControllerTests
{
    private const string Password = "SecurePass123!";
    private const string Url = "/api/v1/admin/dsr";

    private static IAlbaHost Host => AppFixture.Host;

    /// <summary>Registers, signs in, and optionally promotes the account to platform administrator -
    /// which an operator does out of band, so there is deliberately no API for it.</summary>
    private static async Task<string> RegisterAsync(string prefix, bool admin)
    {
        var username = $"{prefix}{Guid.NewGuid():N}"[..15];

        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email = $"{username}-{Guid.NewGuid():N}@example.com",
                Password = Password,
                Username = username,
                BirthDate = DateTime.UtcNow.AddYears(-30),
            }).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        if (admin)
        {
            using var scope = Host.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
            var user = await ctx.Users.FirstAsync(u => u.UserName == username);
            user.UserType = UserType.Admin;
            await ctx.SaveChangesAsync();
        }

        var tokenResult = await Host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = username,
                ["password"] = Password,
                ["client_id"] = "echo",
            }).ToUrl("/connect/token");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        return (await tokenResult.ReadAsJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }

    private static async Task<JsonElement> OpenAsync(string token, OpenDataSubjectRequest request)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(request).ToUrl(Url);
            x.StatusCodeShouldBe(HttpStatusCode.Created);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Open_StampsTheThirtyDayClockAndAuditsTheActingStaffMember()
    {
        var token = await RegisterAsync("dsradm", admin: true);

        var opened = await OpenAsync(token, new OpenDataSubjectRequest
        {
            SubjectEmail = "Someone@Example.com",
            Type = DataSubjectRequestType.Access,
            Notes = "Asked by email for a copy of everything.",
        });

        Assert.Multiple(() =>
        {
            Assert.That(opened.GetProperty("subjectEmail").GetString(), Is.EqualTo("someone@example.com"),
                "lower-cased on intake so the queue does not show the same person twice");
            Assert.That(opened.GetProperty("status").GetString(), Is.EqualTo("Open"));
            Assert.That(opened.GetProperty("disposition").GetString(), Is.EqualTo("None"));
            Assert.That(opened.GetProperty("isOverdue").GetBoolean(), Is.False);
            Assert.That(opened.GetProperty("daysRemaining").GetInt32(), Is.EqualTo(30),
                "thirty days from receipt");
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var audit = await ctx.IdentityAuditEvents
            .Where(a => a.Action == IdentityAuditActions.DsrOpened)
            .ToListAsync();

        Assert.That(audit, Has.Count.EqualTo(1));
        Assert.That(audit[0].Detail, Does.Contain(opened.GetProperty("id").GetString()!),
            "every action is audited with the acting staff id - 'who opened this' has to be "
            + "answerable about a person");
    }

    [Test]
    public async Task Open_MatchesAnExistingAccountByEmailWithoutRequiringOne()
    {
        var token = await RegisterAsync("dsrmatch", admin: true);

        string email;
        using (var scope = Host.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
            email = (await ctx.Users.FirstAsync(u => u.UserType == UserType.Admin)).Email!;
        }

        var matched = await OpenAsync(token, new OpenDataSubjectRequest
        {
            SubjectEmail = email,
            Type = DataSubjectRequestType.Erasure,
        });

        var unmatched = await OpenAsync(token, new OpenDataSubjectRequest
        {
            SubjectEmail = "nobody-here@example.com",
            Type = DataSubjectRequestType.Erasure,
        });

        Assert.Multiple(() =>
        {
            Assert.That(matched.GetProperty("subjectUserId").GetString(), Is.Not.Null);
            Assert.That(unmatched.GetProperty("subjectUserId").ValueKind, Is.EqualTo(JsonValueKind.Null),
                "a request from an address with no account is exactly the case this queue exists "
                + "for - failing to match one is not an error");
        });
    }

    [Test]
    public async Task List_SurfacesOverdueItemsAndCountsThemOverTheWholeTable()
    {
        var token = await RegisterAsync("dsrlist", admin: true);

        await OpenAsync(token, new OpenDataSubjectRequest
        {
            SubjectEmail = "late@example.com",
            Type = DataSubjectRequestType.Access,
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-45),
        });
        await OpenAsync(token, new OpenDataSubjectRequest
        {
            SubjectEmail = "fresh@example.com",
            Type = DataSubjectRequestType.Portability,
        });

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url(Url);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        var requests = body.GetProperty("requests");

        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("overdueCount").GetInt32(), Is.EqualTo(1));
            Assert.That(body.GetProperty("responseWindowDays").GetInt32(), Is.EqualTo(30));
            Assert.That(requests[0].GetProperty("subjectEmail").GetString(), Is.EqualTo("late@example.com"),
                "the soonest deadline sorts first - the thing that must never be buried is the "
                + "request that is about to breach");
            Assert.That(requests[0].GetProperty("isOverdue").GetBoolean(), Is.True);
            Assert.That(requests[0].GetProperty("daysRemaining").GetInt32(), Is.LessThan(0));
        });
    }

    [Test]
    public async Task Patch_ProgressesThenClosesWithADisposition()
    {
        var token = await RegisterAsync("dsrprog", admin: true);
        var opened = await OpenAsync(token, new OpenDataSubjectRequest
        {
            SubjectEmail = "progress@example.com",
            Type = DataSubjectRequestType.Rectification,
        });
        var id = opened.GetProperty("id").GetString()!;

        await PatchAsync(token, id, new UpdateDataSubjectRequest
        {
            Status = DataSubjectRequestStatus.InProgress,
            Note = "Identity verified against the account email.",
        }, HttpStatusCode.OK);

        var closed = await PatchAsync(token, id, new UpdateDataSubjectRequest
        {
            Status = DataSubjectRequestStatus.Closed,
            Disposition = DataSubjectRequestDisposition.Fulfilled,
            Note = "Corrected the display name.",
        }, HttpStatusCode.OK);

        Assert.Multiple(() =>
        {
            Assert.That(closed.GetProperty("status").GetString(), Is.EqualTo("Closed"));
            Assert.That(closed.GetProperty("disposition").GetString(), Is.EqualTo("Fulfilled"));
            Assert.That(closed.GetProperty("closedAt").ValueKind, Is.Not.EqualTo(JsonValueKind.Null));
            Assert.That(closed.GetProperty("isOverdue").GetBoolean(), Is.False);

            // Notes are appended, never replaced: the working notes are the contemporaneous record
            // of what was done, and the last editor must not be able to erase the previous one.
            var notes = closed.GetProperty("notes").GetString()!;
            Assert.That(notes, Does.Contain("Identity verified"));
            Assert.That(notes, Does.Contain("Corrected the display name"));
        });
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Patch_ReopeningAClosedRequest_DoesNotBuyAnotherThirtyDays()
    {
        var token = await RegisterAsync("dsrreop", admin: true);
        var opened = await OpenAsync(token, new OpenDataSubjectRequest
        {
            SubjectEmail = "reopen@example.com",
            Type = DataSubjectRequestType.Objection,
        });
        var id = opened.GetProperty("id").GetString()!;
        var originalDueAt = opened.GetProperty("dueAt").GetDateTimeOffset();

        await PatchAsync(token, id, new UpdateDataSubjectRequest
        {
            Status = DataSubjectRequestStatus.Closed,
            Disposition = DataSubjectRequestDisposition.Refused,
            Note = "Manifestly unfounded.",
        }, HttpStatusCode.OK);

        var reopened = await PatchAsync(token, id, new UpdateDataSubjectRequest
        {
            Status = DataSubjectRequestStatus.InProgress,
            Note = "Requester provided more detail.",
        }, HttpStatusCode.OK);

        Assert.Multiple(() =>
        {
            // Compared with a tolerance: the deadline round-trips through Postgres, whose
            // timestamptz is microsecond-precision, so an exact tick comparison against the value
            // this process created would fail on the storage format rather than on the behaviour.
            Assert.That(reopened.GetProperty("dueAt").GetDateTimeOffset(),
                Is.EqualTo(originalDueAt).Within(TimeSpan.FromSeconds(1)));
            Assert.That(reopened.GetProperty("disposition").GetString(), Is.EqualTo("None"));
            Assert.That(reopened.GetProperty("closedAt").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    [Test]
    public async Task Open_WithABackdatedArrival_ShortensTheRemainingWindow()
    {
        // The clock runs from receipt, not from data entry: a letter opened on Friday may have
        // arrived on Monday, and back-dating it is the honest thing to do.
        var token = await RegisterAsync("dsrback", admin: true);

        var opened = await OpenAsync(token, new OpenDataSubjectRequest
        {
            SubjectEmail = "posted@example.com",
            Type = DataSubjectRequestType.Access,
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-10),
        });

        // Ten of the thirty days were already gone by the time it reached the queue.
        Assert.That(opened.GetProperty("daysRemaining").GetInt32(), Is.InRange(19, 20));
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task EveryRoute_RefusesAnOrdinaryAuthenticatedAccount()
    {
        var staffToken = await RegisterAsync("dsrown", admin: true);
        var opened = await OpenAsync(staffToken, new OpenDataSubjectRequest
        {
            SubjectEmail = "victim@example.com",
            Type = DataSubjectRequestType.Access,
        });
        var id = opened.GetProperty("id").GetString()!;

        var userToken = await RegisterAsync("dsruser", admin: false);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(userToken);
            x.Get.Url(Url);
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });

        await Host.Scenario(x =>
        {
            x.WithBearerToken(userToken);
            x.Post.Json(new OpenDataSubjectRequest
            {
                SubjectEmail = "someone@example.com",
                Type = DataSubjectRequestType.Erasure,
            }).ToUrl(Url);
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });

        await Host.Scenario(x =>
        {
            x.WithBearerToken(userToken);
            x.Patch.Json(new UpdateDataSubjectRequest { Status = DataSubjectRequestStatus.Closed })
                .ToUrl($"{Url}/{id}");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    [Test]
    public async Task List_WithoutAToken_IsUnauthorized()
    {
        await Host.Scenario(x =>
        {
            x.Get.Url(Url);
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });
    }

    [Test]
    public async Task Patch_ClosingWithoutADisposition_Is400()
    {
        // "Closed, outcome unknown" is indistinguishable from "we stopped looking at it".
        var token = await RegisterAsync("dsrnodisp", admin: true);
        var opened = await OpenAsync(token, new OpenDataSubjectRequest
        {
            SubjectEmail = "nodisp@example.com",
            Type = DataSubjectRequestType.Access,
        });
        var id = opened.GetProperty("id").GetString()!;

        await PatchAsync(token, id, new UpdateDataSubjectRequest
        {
            Status = DataSubjectRequestStatus.Closed,
        }, HttpStatusCode.BadRequest);

        await PatchAsync(token, id, new UpdateDataSubjectRequest
        {
            Status = DataSubjectRequestStatus.Closed,
            Disposition = DataSubjectRequestDisposition.None,
        }, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Patch_SettingADispositionWithoutClosing_Is400()
    {
        var token = await RegisterAsync("dsrdisp", admin: true);
        var opened = await OpenAsync(token, new OpenDataSubjectRequest
        {
            SubjectEmail = "openbutdone@example.com",
            Type = DataSubjectRequestType.Access,
        });

        await PatchAsync(token, opened.GetProperty("id").GetString()!, new UpdateDataSubjectRequest
        {
            Status = DataSubjectRequestStatus.InProgress,
            Disposition = DataSubjectRequestDisposition.Fulfilled,
        }, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Open_WithoutAnEmail_Is400()
    {
        var token = await RegisterAsync("dsrnoem", admin: true);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new OpenDataSubjectRequest
            {
                SubjectEmail = "not-an-address",
                Type = DataSubjectRequestType.Access,
            }).ToUrl(Url);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Test]
    public async Task Open_WithAFutureArrivalDate_Is400()
    {
        // Otherwise a request could be given a deadline that never arrives.
        var token = await RegisterAsync("dsrfut", admin: true);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new OpenDataSubjectRequest
            {
                SubjectEmail = "future@example.com",
                Type = DataSubjectRequestType.Access,
                ReceivedAt = DateTimeOffset.UtcNow.AddDays(5),
            }).ToUrl(Url);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Test]
    public async Task Patch_AnUnknownId_Is404()
    {
        var token = await RegisterAsync("dsr404", admin: true);

        await PatchAsync(token, "dsrq_doesnotexist", new UpdateDataSubjectRequest
        {
            Status = DataSubjectRequestStatus.InProgress,
        }, HttpStatusCode.NotFound);
    }

    private static async Task<JsonElement> PatchAsync(
        string token, string id, UpdateDataSubjectRequest update, HttpStatusCode expected)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Patch.Json(update).ToUrl($"{Url}/{id}");
            x.StatusCodeShouldBe(expected);
        });

        return expected == HttpStatusCode.OK ? await result.ReadAsJsonAsync<JsonElement>() : default;
    }

    [TearDown]
    public async Task ClearDatabaseAfterTest()
    {
        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        if (await ctx.DataSubjectRequests.AnyAsync())
        {
            ctx.DataSubjectRequests.RemoveRange(ctx.DataSubjectRequests);
            await ctx.SaveChangesAsync();
        }

        if (await ctx.Users.AnyAsync())
        {
            ctx.Users.RemoveRange(ctx.Users);
            await ctx.SaveChangesAsync();
        }
    }
}
