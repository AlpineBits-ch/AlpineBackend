using Identity.Application.Commands;
using Identity.Contracts.Bus.Commands;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Identity.Tests.Commands;

/// <summary>
/// T1-9: what the purge does to the rows that carry an account's IP addresses.
///
/// <para><b>The rule is scrub, not delete, and both halves are load-bearing.</b> A purged account was
/// leaving an indefinite trail of addresses and device fingerprints bound to an id that is supposed to
/// identify nobody - so the fields go. But the audit log is append-only and its value is precisely
/// that it cannot be erased by whoever is acting on the account; deleting its rows at deletion time
/// would mean the last thing anyone did with a compromised session is also the thing that removes the
/// record of it. So the rows stay.</para>
///
/// <para>Wolverine handler, so no SaveChangesAsync inside it - the tests call SaveChanges themselves
/// to stand in for the transactional middleware, as PurgeUserDataCommandHandlerTests already does.</para>
/// </summary>
[TestFixture]
public class PurgeUserDataScrubTests
{
    private TestIdentityContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestIdentityContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private ApplicationUser SeedUser()
    {
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = $"scrub-{Guid.NewGuid():N}@example.com",
            Username = $"scrub{Guid.NewGuid():N}"[..12],
            PhoneNumber = null!,
            BirthDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-22)),
        });
        _context.Users.Add(user);
        _context.SaveChanges();
        return user;
    }

    private LoginSession SeedSession(string userId, bool revoked = false)
    {
        var session = LoginSession.Create(new CreateLoginSessionParams
        {
            UserId = userId,
            DeviceName = "Test handset",
            DeviceType = DeviceType.Mobile,
            IpAddress = "203.0.113.7",
            UserAgent = "Echo/1.0 (Test)",
        });
        if (revoked) session.Revoke();

        _context.LoginSessions.Add(session);
        _context.SaveChanges();
        return session;
    }

    private IdentityAuditEvent SeedAudit(string userId)
    {
        var audit = IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = userId,
            Action = IdentityAuditActions.BackupRead,
            Detail = "device dev_abc",
            ClientDeviceId = "handset-1",
            IpAddress = "198.51.100.4",
        });
        _context.IdentityAuditEvents.Add(audit);
        _context.SaveChanges();
        return audit;
    }

    private Task PurgeAsync(string userId) =>
        PurgeUserDataCommandHandler.Handle(new PurgeUserDataCommand { UserId = userId }, _context);

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Purge_ScrubsSessionIpAndUserAgent_ButKeepsTheRow()
    {
        var user = SeedUser();
        var session = SeedSession(user.Id);

        await PurgeAsync(user.Id);
        await _context.SaveChangesAsync();

        var reloaded = await _context.LoginSessions.FirstOrDefaultAsync(s => s.Id == session.Id);

        Assert.That(reloaded, Is.Not.Null, "the session row itself must survive the purge");
        Assert.Multiple(() =>
        {
            Assert.That(reloaded!.IpAddress, Is.Null);
            Assert.That(reloaded.UserAgent, Is.Null);
            Assert.That(reloaded.DeviceName, Is.EqualTo("Test handset"),
                "only the personal fields go - the rest of the row is the account's own record of "
                + "where it has been signed in");
        });
    }

    [Test]
    public async Task Purge_ScrubsAuditIp_ButKeepsTheRowAndEverythingElseOnIt()
    {
        var user = SeedUser();
        var audit = SeedAudit(user.Id);

        await PurgeAsync(user.Id);
        await _context.SaveChangesAsync();

        var reloaded = await _context.IdentityAuditEvents.FirstOrDefaultAsync(a => a.Id == audit.Id);

        Assert.That(reloaded, Is.Not.Null,
            "the audit log is append-only; the event is the record and the IP is the incidental "
            + "detail, so deletion of the account must not delete the event");
        Assert.Multiple(() =>
        {
            Assert.That(reloaded!.IpAddress, Is.Null);
            Assert.That(reloaded.Action, Is.EqualTo(IdentityAuditActions.BackupRead));
            Assert.That(reloaded.Detail, Is.EqualTo("device dev_abc"));
            Assert.That(reloaded.ClientDeviceId, Is.EqualTo("handset-1"));
        });
    }

    [Test]
    public async Task Purge_RemovesConsentRecords()
    {
        var user = SeedUser();
        _context.UserConsents.Add(UserConsent.Create(new CreateUserConsentParams
        {
            UserId = user.Id,
            DocumentType = LegalDocumentType.Terms,
            Version = "1.0.0",
            IpAddress = "203.0.113.9",
            AcceptedAt = DateTimeOffset.UtcNow,
        }));
        await _context.SaveChangesAsync();

        await PurgeAsync(user.Id);
        await _context.SaveChangesAsync();

        Assert.That(await _context.UserConsents.CountAsync(c => c.UserId == user.Id), Is.Zero,
            "a consent record proves a particular person agreed to particular terms; once there is "
            + "no person there is nothing left for it to prove, and it carries an IP");
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Purge_IsIdempotentAcrossTheScrubs()
    {
        var user = SeedUser();
        SeedSession(user.Id);
        SeedAudit(user.Id);

        await PurgeAsync(user.Id);
        await _context.SaveChangesAsync();
        await PurgeAsync(user.Id);
        await _context.SaveChangesAsync();

        var sessions = await _context.LoginSessions.CountAsync(s => s.UserId == user.Id);
        var audits = await _context.IdentityAuditEvents.CountAsync(a => a.UserId == user.Id);
        Assert.Multiple(() =>
        {
            Assert.That(sessions, Is.EqualTo(1));
            Assert.That(audits, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Purge_ClearsTheAgeDataOnTheStoredRow()
    {
        var user = SeedUser();

        await PurgeAsync(user.Id);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Users.FirstAsync(u => u.Id == user.Id);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.BirthDate, Is.EqualTo(default(DateOnly)));
            Assert.That(reloaded.AgeVerification.BirthDate, Is.EqualTo(default(DateOnly)));
            Assert.That(reloaded.WasVerifiedAdult, Is.True);
        });
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task Purge_DoesNotTouchAnotherAccountsSessionsOrAuditRows()
    {
        var purged = SeedUser();
        var bystander = SeedUser();
        SeedSession(purged.Id);
        var theirSession = SeedSession(bystander.Id);
        var theirAudit = SeedAudit(bystander.Id);

        await PurgeAsync(purged.Id);
        await _context.SaveChangesAsync();

        var session = await _context.LoginSessions.FirstAsync(s => s.Id == theirSession.Id);
        var audit = await _context.IdentityAuditEvents.FirstAsync(a => a.Id == theirAudit.Id);

        Assert.Multiple(() =>
        {
            Assert.That(session.IpAddress, Is.EqualTo("203.0.113.7"));
            Assert.That(session.UserAgent, Is.EqualTo("Echo/1.0 (Test)"));
            Assert.That(audit.IpAddress, Is.EqualTo("198.51.100.4"));
        });
    }

    [Test]
    public async Task Purge_DoesNotDeleteRevokedSessionRows()
    {
        // Retention deletes revoked sessions after 180 days (T1-8). The purge does not - it scrubs.
        // Conflating the two would mean a deletion request silently destroyed the account's own
        // record of the sessions it had cut off.
        var user = SeedUser();
        var revoked = SeedSession(user.Id, revoked: true);

        await PurgeAsync(user.Id);
        await _context.SaveChangesAsync();

        var reloaded = await _context.LoginSessions.FirstOrDefaultAsync(s => s.Id == revoked.Id);
        Assert.That(reloaded, Is.Not.Null);
        Assert.That(reloaded!.IpAddress, Is.Null);
    }
}
