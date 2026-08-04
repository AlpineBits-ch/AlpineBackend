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

/// <summary>
/// <c>api/v1/legal/*</c> over real HTTP, real auth and real Postgres (T1-10 / T1-12).
///
/// <para>The documents these run against are the placeholders in <c>docs/legal</c>, seeded into the
/// database by <c>LegalDocumentSeeder</c> when the host starts - so these also prove that the
/// build-output copy and the startup seed actually work, which no unit test can.</para>
/// </summary>
[TestFixture]
public class LegalControllerTests
{
    private const string Password = "SecurePass123!";

    private static IAlbaHost Host => AppFixture.Host;

    private static async Task<(string Token, string Username)> RegisterAndLoginAsync(string prefix, int ageYears = 25)
    {
        var username = $"{prefix}{Guid.NewGuid():N}"[..15];

        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email = $"{username}-{Guid.NewGuid():N}@example.com",
                Password = Password,
                Username = username,
                BirthDate = DateTime.UtcNow.AddYears(-ageYears),
            }).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

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

        var body = await tokenResult.ReadAsJsonAsync<JsonElement>();
        return (body.GetProperty("access_token").GetString()!, username);
    }

    // ── documents ───────────────────────────────────────────────────────────

    [Test]
    public async Task GetDocuments_IsAnonymous_AndListsTheSeededPlaceholders()
    {
        // The terms have to be readable before there is an account to read them with - a
        // registration screen that cannot show what is being agreed to is not consent.
        var result = await Host.Scenario(x =>
        {
            x.Get.Url("/api/v1/legal/documents");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var documents = await result.ReadAsJsonAsync<JsonElement>();
        var types = documents.EnumerateArray()
            .Select(d => d.GetProperty("documentType").GetString())
            .ToList();

        Assert.That(types, Is.EquivalentTo(new[] { "Terms", "Privacy", "Cookies" }));

        foreach (var document in documents.EnumerateArray())
        {
            Assert.That(document.GetProperty("contentHash").GetString(), Has.Length.EqualTo(64),
                "the hash is published so a user or an auditor can verify the document they were "
                + "shown is the one the consent record names");
        }
    }

    [Test]
    public async Task GetDocumentContent_ServesTheBytesThatWereHashed()
    {
        var listResult = await Host.Scenario(x =>
        {
            x.Get.Url("/api/v1/legal/documents");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        var terms = (await listResult.ReadAsJsonAsync<JsonElement>()).EnumerateArray()
            .First(d => d.GetProperty("documentType").GetString() == "Terms");

        var version = terms.GetProperty("version").GetString();

        var contentResult = await Host.Scenario(x =>
        {
            x.Get.Url($"/api/v1/legal/documents/terms/{Uri.EscapeDataString(version!)}");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var content = contentResult.ReadAsText();

        Assert.Multiple(() =>
        {
            // Was a check for the placeholder banner, back when what shipped was a generated
            // outline. The documents are now the real published text, so this asserts the
            // endpoint served that rather than an unreviewed stand-in - the hash check below
            // proves the bytes are intact, but not that they are the right document.
            Assert.That(content, Does.Contain("End User License Agreement"));
            Assert.That(content, Does.Not.Contain("LEGAL REVIEW REQUIRED"));
            Assert.That(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
                Is.EqualTo(terms.GetProperty("contentHash").GetString()),
                "the URL in the row must serve exactly the bytes the row's hash was taken over");
        });
    }

    [Test]
    public async Task GetDocumentContent_ForAnUnknownVersion_Is404()
    {
        await Host.Scenario(x =>
        {
            x.Get.Url("/api/v1/legal/documents/terms/99.99.99");
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    [Test]
    public async Task GetDocumentContent_ForAnUnknownDocumentType_Is404()
    {
        await Host.Scenario(x =>
        {
            x.Get.Url("/api/v1/legal/documents/marketing/1.0.0");
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    // ── consents ────────────────────────────────────────────────────────────

    [Test]
    public async Task Registration_RecordsTermsAndPrivacyConsentWithTheOriginatingIp()
    {
        var (token, username) = await RegisterAndLoginAsync("lgreg");

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url("/api/v1/legal/consents");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var consents = await result.ReadAsJsonAsync<JsonElement>();
        var types = consents.EnumerateArray()
            .Select(c => c.GetProperty("documentType").GetString())
            .ToList();

        Assert.That(types, Is.EquivalentTo(new[] { "Terms", "Privacy" }),
            "an account that exists without a consent record is an account we cannot show ever "
            + "agreed to anything");

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.FirstAsync(u => u.UserName == username);
        var stored = await ctx.UserConsents.Where(c => c.UserId == user.Id).ToListAsync();

        var current = await ctx.LegalDocuments.ToListAsync();
        Assert.That(stored.Select(c => (c.DocumentType, c.Version)),
            Is.EquivalentTo(current
                .Where(d => d.DocumentType != LegalDocumentType.Cookies)
                .Select(d => (d.DocumentType, d.Version))),
            "the consent has to name the version that was current at signup - a record that does "
            + "not say which text was agreed to is not a record of anything");

        // The IP itself is not asserted here: Alba hosts the app in-process over a TestServer, which
        // has no remote endpoint, so HttpContext.Connection.RemoteIpAddress is legitimately null. The
        // plumbing that carries it from the controller to the consent row is asserted directly in
        // Identity.Tests/Commands/RegistrationConsentTests instead.
    }

    [Test]
    public async Task GetConsents_ResponseDoesNotEchoTheStoredIp()
    {
        // Held by the operator as evidence; echoing it on a route reachable with a stolen session
        // would turn the consent log into a history of where the account holder has been.
        var (token, _) = await RegisterAndLoginAsync("lgip");

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url("/api/v1/legal/consents");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        Assert.That(result.ReadAsText(), Does.Not.Contain("ipAddress"));
    }

    [Test]
    public async Task PostConsent_ForAlreadyAcceptedTerms_IsIdempotent()
    {
        var (token, username) = await RegisterAndLoginAsync("lgidem");

        string version;
        using (var scope = Host.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
            version = (await ctx.LegalDocuments.FirstAsync(d => d.DocumentType == LegalDocumentType.Terms)).Version;
        }

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new RecordConsentRequest { DocumentType = LegalDocumentType.Terms, Version = version })
                .ToUrl("/api/v1/legal/consents");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var check = Host.Services.CreateScope();
        var context = check.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await context.Users.FirstAsync(u => u.UserName == username);

        Assert.That(
            await context.UserConsents.CountAsync(
                c => c.UserId == user.Id && c.DocumentType == LegalDocumentType.Terms),
            Is.EqualTo(1),
            "a retried acceptance is not a second decision");
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task PostConsent_WithoutAToken_IsUnauthorized()
    {
        await Host.Scenario(x =>
        {
            x.Post.Json(new RecordConsentRequest { DocumentType = LegalDocumentType.Terms, Version = "1.0.0" })
                .ToUrl("/api/v1/legal/consents");
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });
    }

    [Test]
    public async Task PostConsent_ForAVersionThatWasNeverPublished_Is404()
    {
        // A client that could name any string could satisfy an outstanding consent by inventing a
        // version number, which would make the whole record worthless.
        var (token, _) = await RegisterAndLoginAsync("lgfake");

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new RecordConsentRequest { DocumentType = LegalDocumentType.Terms, Version = "99.99.99" })
                .ToUrl("/api/v1/legal/consents");
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    [Test]
    public async Task PostConsent_WithNoVersion_Is400()
    {
        var (token, _) = await RegisterAndLoginAsync("lgnover");

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new RecordConsentRequest { DocumentType = LegalDocumentType.Terms, Version = "" })
                .ToUrl("/api/v1/legal/consents");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Test]
    public async Task DeleteConsent_RefusesAndSaysWhatToDoInstead()
    {
        // T1-10: Terms/Privacy are not withdrawable while the account is active, and the client
        // should say so rather than leaving the user to discover a dead button.
        var (token, _) = await RegisterAndLoginAsync("lgwd");

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Delete.Url("/api/v1/legal/consents/Terms");
            x.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("code").GetString(), Is.EqualTo("consent_not_withdrawable"));
            Assert.That(body.GetProperty("deleteAccount").GetString(), Is.EqualTo("/api/v1/users/self"));
        });
    }

    // ── the self payload ────────────────────────────────────────────────────

    [Test]
    public async Task Self_ConsentRequired_IsEmptyForAFreshAccountAndPopulatesWhenAVersionIsPublished()
    {
        var (token, username) = await RegisterAndLoginAsync("lgself");

        var before = await SelfAsync(token);
        Assert.That(before.GetProperty("consentRequired").GetArrayLength(), Is.Zero,
            "registration already recorded consent for the current versions");

        // Publish a newer Terms. The existing consent must be left alone and the account must start
        // showing an outstanding one.
        using (var scope = Host.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
            ctx.LegalDocuments.Add(LegalDocument.Create(new CreateLegalDocumentParams
            {
                DocumentType = LegalDocumentType.Terms,
                Version = "test-next",
                EffectiveAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                ContentHash = new string('b', 64),
                Url = "https://example.test/legal/terms/test-next",
            }));
            await ctx.SaveChangesAsync();
        }

        try
        {
            var after = await SelfAsync(token);
            var outstanding = after.GetProperty("consentRequired");

            Assert.That(outstanding.GetArrayLength(), Is.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(outstanding[0].GetProperty("documentType").GetString(), Is.EqualTo("Terms"));
                Assert.That(outstanding[0].GetProperty("version").GetString(), Is.EqualTo("test-next"));
                Assert.That(outstanding[0].GetProperty("url").GetString(), Is.Not.Empty);
            });

            // Accepting it clears the state, and the old consent is still there.
            await Host.Scenario(x =>
            {
                x.WithBearerToken(token);
                x.Post.Json(new RecordConsentRequest
                {
                    DocumentType = LegalDocumentType.Terms,
                    Version = "test-next",
                }).ToUrl("/api/v1/legal/consents");
                x.StatusCodeShouldBe(HttpStatusCode.OK);
            });

            var settled = await SelfAsync(token);
            Assert.That(settled.GetProperty("consentRequired").GetArrayLength(), Is.Zero);

            using var scope = Host.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
            var user = await ctx.Users.FirstAsync(u => u.UserName == username);
            var stored = await ctx.UserConsents
                .Where(c => c.UserId == user.Id && c.DocumentType == LegalDocumentType.Terms)
                .ToListAsync();

            Assert.That(stored, Has.Count.EqualTo(2),
                "publishing a new version leaves the old consent intact - two versions accepted "
                + "means two records of what was shown");
        }
        finally
        {
            using var scope = Host.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
            var extra = await ctx.LegalDocuments.Where(d => d.Version == "test-next").ToListAsync();
            ctx.LegalDocuments.RemoveRange(extra);
            await ctx.SaveChangesAsync();
        }
    }

    [Test]
    public async Task Self_StillCarriesTheLegacyBlocksAlongsideTheNewKey()
    {
        // Additive-only. A v1 client parsing this payload must be unaffected by consentRequired.
        var (token, _) = await RegisterAndLoginAsync("lgadd");

        var self = await SelfAsync(token);

        Assert.Multiple(() =>
        {
            Assert.That(self.TryGetProperty("userPreferences", out _), Is.True);
            Assert.That(self.TryGetProperty("privacySettings", out _), Is.True);
            Assert.That(self.TryGetProperty("consentRequired", out _), Is.True);
        });
    }

    private static async Task<JsonElement> SelfAsync(string token)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url("/api/v1/users/self");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
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

    /// <summary>
    /// The seeder upserts and, until this was fixed, never removed - so a database that had seen an
    /// earlier manifest kept rows for documents this build no longer ships. That is not merely
    /// untidy: the placeholder rows were stamped with the date they were seeded, which is *later*
    /// than the real Terms (2026-05-19) and Cookies (2025-05-17) documents that replaced them, and
    /// "current" is the latest EffectiveAt. The stale placeholder went on being served as the
    /// current Terms of Service, and its file was gone, so fetching it 404'd.
    /// </summary>
    [Test]
    public async Task Seeder_RemovesDocumentsTheManifestNoLongerDeclares_AndTheRealOneBecomesCurrent()
    {
        using (var arrange = Host.Services.CreateScope())
        {
            var ctx = arrange.ServiceProvider.GetRequiredService<MicroserviceContext>();
            ctx.LegalDocuments.Add(LegalDocument.Create(new CreateLegalDocumentParams
            {
                DocumentType = LegalDocumentType.Terms,
                Version = "0.1.0-placeholder",
                // Dated ahead of the real document, exactly as the seeded placeholders were.
                EffectiveAt = DateTimeOffset.UtcNow.AddDays(-1),
                ContentHash = new string('0', 64),
                Url = "https://example.test/api/v1/identity/legal/documents/terms/0.1.0-placeholder",
            }));
            await ctx.SaveChangesAsync();
        }

        var seeder = new Identity.Application.Services.LegalDocumentSeeder(
            Host.Services.GetRequiredService<IServiceScopeFactory>(),
            Host.Services.GetRequiredService<Identity.Application.Services.LegalDocumentCatalog>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                Identity.Application.Services.LegalDocumentSeeder>.Instance);

        await seeder.StartAsync(CancellationToken.None);

        using var assert = Host.Services.CreateScope();
        var after = assert.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var terms = await after.LegalDocuments
            .Where(d => d.DocumentType == LegalDocumentType.Terms)
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(terms.Select(t => t.Version), Does.Not.Contain("0.1.0-placeholder"),
                "a document the manifest no longer declares has no file behind it, so leaving the "
                + "row serves a 404 and lets it outrank the document that replaced it");
            Assert.That(terms.Select(t => t.Version), Does.Contain("2026-05-19"));
        });

        var listed = await Host.Scenario(x =>
        {
            x.Get.Url("/api/v1/legal/documents");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        var currentTerms = (await listed.ReadAsJsonAsync<JsonElement>()).EnumerateArray()
            .First(d => d.GetProperty("documentType").GetString() == "Terms");

        Assert.That(currentTerms.GetProperty("version").GetString(), Is.EqualTo("2026-05-19"),
            "the current Terms must be the real published document, not a stale placeholder");
    }
}
