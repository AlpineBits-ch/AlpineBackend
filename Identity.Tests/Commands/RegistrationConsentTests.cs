using Identity.Application.Commands;
using Identity.Application.Services;
using Identity.Contracts.Commands;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Identity.Tests.Commands;

/// <summary>
/// T1-10: registration records consent for the then-current Terms and Privacy versions, with the
/// IP.
/// </summary>
[TestFixture]
public class RegistrationConsentTests
{
    private TestIdentityContext _context = null!;
    private static readonly DateTimeOffset Published = DateTimeOffset.UtcNow.AddDays(-30);

    [SetUp]
    public void SetUp() => _context = new TestIdentityContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private void SeedDocument(LegalDocumentType type, string version, DateTimeOffset? effectiveAt = null)
    {
        _context.LegalDocuments.Add(LegalDocument.Create(new CreateLegalDocumentParams
        {
            DocumentType = type,
            Version = version,
            EffectiveAt = effectiveAt ?? Published,
            ContentHash = new string('c', 64),
            Url = $"https://example.test/legal/{type}/{version}".ToLowerInvariant(),
        }));
        _context.SaveChanges();
    }

    private async Task<CreateUserResponse> RegisterAsync(string? ipAddress)
    {
        var response = await CreateUserCommandHandler.Handle(
            new CreateUserCommand
            {
                Email = $"reg-{Guid.NewGuid():N}@example.com",
                Username = $"reg{Guid.NewGuid():N}"[..12],
                Password = "SecurePass123!",
                BirthDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-25)),
                IpAddress = ipAddress,
            },
            NullLogger<CreateUserCommandHandler>.Instance,
            _context,
            new PasswordHasher<ApplicationUser>(Options.Create(new PasswordHasherOptions())),
            new ConsentService(_context));

        await _context.SaveChangesAsync();
        return response;
    }

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Register_RecordsTermsAndPrivacyConsentWithTheOriginatingAddress()
    {
        SeedDocument(LegalDocumentType.Terms, "1.0.0");
        SeedDocument(LegalDocumentType.Privacy, "1.0.0");

        var response = await RegisterAsync("203.0.113.42");

        var consents = await _context.UserConsents
            .Where(c => c.UserId == response.UserId)
            .ToListAsync();

        Assert.That(consents, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(consents.Select(c => c.DocumentType),
                Is.EquivalentTo(new[] { LegalDocumentType.Terms, LegalDocumentType.Privacy }));
            Assert.That(consents.All(c => c.Version == "1.0.0"), Is.True);
            Assert.That(consents.All(c => c.IpAddress == "203.0.113.42"), Is.True,
                "a consent record without an origin is materially weaker evidence");
        });
    }

    [Test]
    public async Task Register_RecordsTheVersionThatIsCurrentAtSignup_NotTheOldest()
    {
        SeedDocument(LegalDocumentType.Terms, "1.0.0", Published);
        SeedDocument(LegalDocumentType.Terms, "2.0.0", DateTimeOffset.UtcNow.AddDays(-1));
        SeedDocument(LegalDocumentType.Privacy, "1.0.0", Published);

        var response = await RegisterAsync("203.0.113.42");

        var terms = await _context.UserConsents.SingleAsync(
            c => c.UserId == response.UserId && c.DocumentType == LegalDocumentType.Terms);

        Assert.That(terms.Version, Is.EqualTo("2.0.0"));
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Register_WithNoAddressAvailable_StillRecordsTheConsent()
    {
        // A caller that genuinely has no address records the consent without one, rather than not
        // recording the consent. The account existing without any record is the worse outcome.
        SeedDocument(LegalDocumentType.Terms, "1.0.0");
        SeedDocument(LegalDocumentType.Privacy, "1.0.0");

        var response = await RegisterAsync(null);

        var consents = await _context.UserConsents.Where(c => c.UserId == response.UserId).ToListAsync();
        Assert.That(consents, Has.Count.EqualTo(2));
        Assert.That(consents.All(c => c.IpAddress is null), Is.True);
    }

    [Test]
    public async Task Register_OnAnInstanceWithNoPublishedDocuments_StillSucceeds()
    {
        var response = await RegisterAsync("203.0.113.42");

        var recorded = await _context.UserConsents.CountAsync();
        Assert.Multiple(() =>
        {
            Assert.That(response.UserId, Is.Not.Null);
            Assert.That(recorded, Is.Zero);
        });
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task Register_DoesNotRecordConsentForOptionalDocuments()
    {
        SeedDocument(LegalDocumentType.Terms, "1.0.0");
        SeedDocument(LegalDocumentType.Privacy, "1.0.0");
        SeedDocument(LegalDocumentType.Cookies, "1.0.0");

        var response = await RegisterAsync("203.0.113.42");

        Assert.That(
            await _context.UserConsents.AnyAsync(
                c => c.UserId == response.UserId && c.DocumentType == LegalDocumentType.Cookies),
            Is.False,
            "signing up is not consent to analytics storage, and recording it as though it were "
            + "would be a false record rather than a missing one");
    }

    [Test]
    public async Task Register_DoesNotRecordConsentForAFutureDatedVersion()
    {
        SeedDocument(LegalDocumentType.Terms, "1.0.0", Published);
        SeedDocument(LegalDocumentType.Terms, "2.0.0", DateTimeOffset.UtcNow.AddDays(30));
        SeedDocument(LegalDocumentType.Privacy, "1.0.0", Published);

        var response = await RegisterAsync("203.0.113.42");

        var terms = await _context.UserConsents.SingleAsync(
            c => c.UserId == response.UserId && c.DocumentType == LegalDocumentType.Terms);

        Assert.That(terms.Version, Is.EqualTo("1.0.0"),
            "an account cannot agree to something that is not yet in force");
    }
}
