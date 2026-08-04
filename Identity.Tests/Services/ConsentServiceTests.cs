using Identity.Application.Services;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Identity.Tests.Services;

/// <summary>
/// T1-10. No record existed anywhere that a user had ever accepted anything; these cover what the
/// record now means.
///
/// <para>The two behaviours worth breaking a build over: publishing a new version must leave existing
/// consents intact (they are evidence of what was shown, not a pointer to the current text), and the
/// "current" version must be chosen by effective date rather than by string order - a semver-shaped
/// string sorted as text puts 1.10.0 before 1.9.0, and the failure would present as an instance
/// quietly demanding consent to a superseded document.</para>
/// </summary>
[TestFixture]
public class ConsentServiceTests
{
    private TestIdentityContext _context = null!;
    private ConsentService _consents = null!;
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [SetUp]
    public void SetUp()
    {
        _context = new TestIdentityContext(Guid.NewGuid().ToString());
        _consents = new ConsentService(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private ApplicationUser SeedUser()
    {
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = $"consent-{Guid.NewGuid():N}@example.com",
            Username = $"cns{Guid.NewGuid():N}"[..12],
            PhoneNumber = null!,
            BirthDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-22)),
        });
        _context.Users.Add(user);
        _context.SaveChanges();
        return user;
    }

    private void SeedDocument(LegalDocumentType type, string version, DateTimeOffset effectiveAt)
    {
        _context.LegalDocuments.Add(LegalDocument.Create(new CreateLegalDocumentParams
        {
            DocumentType = type,
            Version = version,
            EffectiveAt = effectiveAt,
            ContentHash = new string('a', 64),
            Url = $"https://example.test/legal/{type}/{version}".ToLowerInvariant(),
        }));
        _context.SaveChanges();
    }

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task GetCurrentDocuments_ReturnsTheLatestEffectiveVersionOfEachType()
    {
        SeedDocument(LegalDocumentType.Terms, "1.0.0", Now.AddDays(-100));
        SeedDocument(LegalDocumentType.Terms, "2.0.0", Now.AddDays(-1));
        SeedDocument(LegalDocumentType.Privacy, "1.0.0", Now.AddDays(-100));

        var current = await _consents.GetCurrentDocumentsAsync(Now);

        Assert.Multiple(() =>
        {
            Assert.That(current.Single(d => d.DocumentType == LegalDocumentType.Terms).Version,
                Is.EqualTo("2.0.0"));
            Assert.That(current.Single(d => d.DocumentType == LegalDocumentType.Privacy).Version,
                Is.EqualTo("1.0.0"));
        });
    }

    [Test]
    public async Task GetOutstanding_ForAnUpToDateAccount_IsEmpty()
    {
        var user = SeedUser();
        SeedDocument(LegalDocumentType.Terms, "1.0.0", Now.AddDays(-10));
        SeedDocument(LegalDocumentType.Privacy, "1.0.0", Now.AddDays(-10));

        await _consents.RecordAsync(user.Id, LegalDocumentType.Terms, "1.0.0", "203.0.113.1", Now);
        await _consents.RecordAsync(user.Id, LegalDocumentType.Privacy, "1.0.0", "203.0.113.1", Now);
        await _context.SaveChangesAsync();

        Assert.That(await _consents.GetOutstandingAsync(user.Id, Now), Is.Empty);
    }

    [Test]
    public async Task PublishingANewVersion_LeavesTheOldConsentIntactAndMarksTheAccountAsOwingOne()
    {
        var user = SeedUser();
        SeedDocument(LegalDocumentType.Terms, "1.0.0", Now.AddDays(-100));
        SeedDocument(LegalDocumentType.Privacy, "1.0.0", Now.AddDays(-100));
        await _consents.RecordAsync(user.Id, LegalDocumentType.Terms, "1.0.0", "203.0.113.1", Now.AddDays(-99));
        await _consents.RecordAsync(user.Id, LegalDocumentType.Privacy, "1.0.0", "203.0.113.1", Now.AddDays(-99));
        await _context.SaveChangesAsync();

        SeedDocument(LegalDocumentType.Terms, "2.0.0", Now.AddDays(-1));

        var outstanding = await _consents.GetOutstandingAsync(user.Id, Now);
        var stored = await _consents.GetConsentsAsync(user.Id);

        Assert.Multiple(() =>
        {
            Assert.That(outstanding.Select(o => (o.DocumentType, o.Version)),
                Is.EquivalentTo(new[] { (LegalDocumentType.Terms, "2.0.0") }));

            Assert.That(stored.Any(c => c.DocumentType == LegalDocumentType.Terms && c.Version == "1.0.0"),
                Is.True,
                "the old consent is the evidence of what the user was actually shown - rewriting or "
                + "deleting it on publication would destroy the only record of it");
        });
    }

    [Test]
    public async Task RecordAsync_StoresTheIpAndTheAcceptanceTime()
    {
        var user = SeedUser();

        await _consents.RecordAsync(user.Id, LegalDocumentType.Terms, "1.0.0", "198.51.100.9", Now);
        await _context.SaveChangesAsync();

        var stored = await _context.UserConsents.SingleAsync(c => c.UserId == user.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored.IpAddress, Is.EqualTo("198.51.100.9"));
            Assert.That(stored.AcceptedAt, Is.EqualTo(Now));
        });
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetCurrentDocuments_IgnoresAFutureDatedVersion()
    {
        SeedDocument(LegalDocumentType.Terms, "1.0.0", Now.AddDays(-100));
        SeedDocument(LegalDocumentType.Terms, "2.0.0", Now.AddDays(30));

        var current = await _consents.GetCurrentDocumentsAsync(Now);

        Assert.That(current.Single().Version, Is.EqualTo("1.0.0"),
            "a version can be published and announced before it binds");
    }

    [Test]
    public async Task GetCurrentDocuments_OrdersByEffectiveDate_NotByVersionString()
    {
        SeedDocument(LegalDocumentType.Terms, "1.9.0", Now.AddDays(-100));
        SeedDocument(LegalDocumentType.Terms, "1.10.0", Now.AddDays(-1));

        var current = await _consents.GetCurrentDocumentsAsync(Now);

        Assert.That(current.Single().Version, Is.EqualTo("1.10.0"),
            "ordered as text, 1.10.0 sorts before 1.9.0 - which would make the current document a "
            + "superseded one, silently");
    }

    [Test]
    public async Task RecordAsync_TwiceForTheSameVersion_KeepsTheOriginalRecord()
    {
        var user = SeedUser();

        await _consents.RecordAsync(user.Id, LegalDocumentType.Terms, "1.0.0", "203.0.113.1", Now);
        await _context.SaveChangesAsync();

        // The retry of a request whose response the client never saw.
        await _consents.RecordAsync(user.Id, LegalDocumentType.Terms, "1.0.0", "203.0.113.99", Now.AddDays(1));
        await _context.SaveChangesAsync();

        var stored = await _context.UserConsents.Where(c => c.UserId == user.Id).ToListAsync();
        Assert.That(stored, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(stored[0].AcceptedAt, Is.EqualTo(Now),
                "the evidence is of the moment the user actually agreed, not of the last retry");
            Assert.That(stored[0].IpAddress, Is.EqualTo("203.0.113.1"));
        });
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task GetOutstanding_WithNoPublishedDocuments_IsEmpty()
    {
        var user = SeedUser();

        Assert.That(await _consents.GetOutstandingAsync(user.Id, Now), Is.Empty,
            "an instance that has published nothing demands nothing - the alternative is every "
            + "client blocked on a consent there is no document to show");
    }

    [Test]
    public async Task GetOutstanding_DoesNotDemandOptionalDocuments()
    {
        var user = SeedUser();
        SeedDocument(LegalDocumentType.Cookies, "1.0.0", Now.AddDays(-1));

        Assert.That(await _consents.GetOutstandingAsync(user.Id, Now), Is.Empty,
            "there is no lawful reading in which continued use of the service is contingent on "
            + "accepting analytics storage");
    }

    [Test]
    public async Task GetOutstanding_IsScopedToTheAccount()
    {
        var accepted = SeedUser();
        var didNot = SeedUser();
        SeedDocument(LegalDocumentType.Terms, "1.0.0", Now.AddDays(-1));
        SeedDocument(LegalDocumentType.Privacy, "1.0.0", Now.AddDays(-1));

        await _consents.RecordAsync(accepted.Id, LegalDocumentType.Terms, "1.0.0", null, Now);
        await _consents.RecordAsync(accepted.Id, LegalDocumentType.Privacy, "1.0.0", null, Now);
        await _context.SaveChangesAsync();

        var forAccepted = await _consents.GetOutstandingAsync(accepted.Id, Now);
        var forOther = await _consents.GetOutstandingAsync(didNot.Id, Now);

        Assert.Multiple(() =>
        {
            Assert.That(forAccepted, Is.Empty);
            Assert.That(forOther, Has.Count.EqualTo(2));
        });
    }
}
