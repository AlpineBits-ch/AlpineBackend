using AppEnvironment;
using Identity.Application.Services;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Identity.Tests.Services;

/// <summary>
/// T1-8. Before this sweep existed, nothing in the system had a TTL: login IPs, user agents and audit
/// IPs accumulated for the life of the account.
///
/// <para>The assertions that matter here are the ones about what is <i>kept</i>. Every one of these
/// windows is a scrub except the single delete, and a sweep that quietly removed an audit row would
/// have turned a retention policy into an erasure of the trail that detects account takeover.</para>
///
/// <para>Times are injected rather than faked globally: <see cref="RetentionSweep.RunAsync"/> takes
/// "now" as a parameter precisely so a ninety-day boundary can be tested without waiting or without a
/// clock abstraction nothing else in this codebase has.</para>
/// </summary>
[TestFixture]
public class RetentionSweepTests
{
    private TestIdentityContext _context = null!;
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [SetUp]
    public void SetUp() => _context = new TestIdentityContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private ApplicationUser SeedUser()
    {
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = $"ret-{Guid.NewGuid():N}@example.com",
            Username = $"ret{Guid.NewGuid():N}"[..12],
            PhoneNumber = null!,
            BirthDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-22)),
        });
        _context.Users.Add(user);
        _context.SaveChanges();
        return user;
    }

    private LoginSession SeedSession(string userId, int ageDays, int? revokedDaysAgo = null)
    {
        var session = LoginSession.Create(new CreateLoginSessionParams
        {
            UserId = userId,
            DeviceName = "Handset",
            DeviceType = DeviceType.Mobile,
            IpAddress = "203.0.113.7",
            UserAgent = "Echo/1.0",
        });
        session.CreatedAt = Now.AddDays(-ageDays);
        session.UpdatedAt = session.CreatedAt;
        session.LastUsedAt = Now;   // deliberately fresh - see the LastUsedAt test below
        if (revokedDaysAgo is not null) session.RevokedAt = Now.AddDays(-revokedDaysAgo.Value);

        _context.LoginSessions.Add(session);
        _context.SaveChanges();
        return session;
    }

    private IdentityAuditEvent SeedAudit(string userId, int ageDays)
    {
        var audit = IdentityAuditEvent.Create(new CreateIdentityAuditEventParams
        {
            UserId = userId,
            Action = IdentityAuditActions.BackupRead,
            Detail = "device dev_abc",
            IpAddress = "198.51.100.4",
            CreatedAt = Now.AddDays(-ageDays),
        });
        _context.IdentityAuditEvents.Add(audit);
        _context.SaveChanges();
        return audit;
    }

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Sweep_ScrubsALoginSessionOlderThanNinetyDays_AndKeepsTheRow()
    {
        var user = SeedUser();
        var session = SeedSession(user.Id, ageDays: 100);

        var result = await RetentionSweep.RunAsync(_context, Now);

        var reloaded = await _context.LoginSessions.FirstOrDefaultAsync(s => s.Id == session.Id);
        Assert.That(reloaded, Is.Not.Null, "the row is kept; only the IP and user agent age out");
        Assert.Multiple(() =>
        {
            Assert.That(reloaded!.IpAddress, Is.Null);
            Assert.That(reloaded.UserAgent, Is.Null);
            Assert.That(reloaded.DeviceName, Is.EqualTo("Handset"));
            Assert.That(result.LoginSessionsScrubbed, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Sweep_ScrubsAnAuditIpOlderThanOneHundredEightyDays_AndKeepsEverythingElse()
    {
        var user = SeedUser();
        var audit = SeedAudit(user.Id, ageDays: 200);

        var result = await RetentionSweep.RunAsync(_context, Now);

        var reloaded = await _context.IdentityAuditEvents.FirstOrDefaultAsync(a => a.Id == audit.Id);
        Assert.That(reloaded, Is.Not.Null, "audit rows are kept FOREVER - only the IP column ages out");
        Assert.Multiple(() =>
        {
            Assert.That(reloaded!.IpAddress, Is.Null);
            Assert.That(reloaded.Action, Is.EqualTo(IdentityAuditActions.BackupRead));
            Assert.That(reloaded.Detail, Is.EqualTo("device dev_abc"));
            Assert.That(result.AuditEventIpsScrubbed, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Sweep_DeletesARevokedSessionOlderThanOneHundredEightyDays()
    {
        var user = SeedUser();
        var session = SeedSession(user.Id, ageDays: 400, revokedDaysAgo: 200);

        var result = await RetentionSweep.RunAsync(_context, Now);

        var stillThere = await _context.LoginSessions.AnyAsync(s => s.Id == session.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stillThere, Is.False);
            Assert.That(result.RevokedLoginSessionsDeleted, Is.EqualTo(1));
        });
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Sweep_LeavesASessionOneDayInsideTheWindowAlone()
    {
        var user = SeedUser();
        var session = SeedSession(user.Id, ageDays: 89);

        await RetentionSweep.RunAsync(_context, Now);

        var reloaded = await _context.LoginSessions.FirstAsync(s => s.Id == session.Id);
        Assert.That(reloaded.IpAddress, Is.EqualTo("203.0.113.7"),
            "a boundary that is off by a day is a boundary nobody can reason about");
    }

    [Test]
    public async Task Sweep_LeavesAnAuditRowOneDayInsideTheWindowAlone()
    {
        var user = SeedUser();
        var audit = SeedAudit(user.Id, ageDays: 179);

        await RetentionSweep.RunAsync(_context, Now);

        var reloaded = await _context.IdentityAuditEvents.FirstAsync(a => a.Id == audit.Id);
        Assert.That(reloaded.IpAddress, Is.EqualTo("198.51.100.4"));
    }

    [Test]
    public async Task Sweep_MeasuresTheSessionWindowFromCreation_NotFromLastUse()
    {
        // A session refreshed daily for a year would otherwise keep a year-old login IP forever,
        // which is exactly the data the window exists to age out. SeedSession stamps LastUsedAt to
        // "now" for every row, so this passing at all proves the predicate is on CreatedAt.
        var user = SeedUser();
        var session = SeedSession(user.Id, ageDays: 365);

        await RetentionSweep.RunAsync(_context, Now);

        var reloaded = await _context.LoginSessions.FirstAsync(s => s.Id == session.Id);
        Assert.That(reloaded.IpAddress, Is.Null);
    }

    [Test]
    public async Task Sweep_RunTwice_IsAQuietNoOpTheSecondTime()
    {
        var user = SeedUser();
        SeedSession(user.Id, ageDays: 100);
        SeedAudit(user.Id, ageDays: 200);

        await RetentionSweep.RunAsync(_context, Now);
        var second = await RetentionSweep.RunAsync(_context, Now);

        Assert.That(second.Total, Is.Zero,
            "already-scrubbed rows must not be re-selected on every tick forever");
    }

    [Test]
    public async Task Sweep_ARevokedSessionOldEnoughForBoth_IsDeletedAndNotDoubleCounted()
    {
        var user = SeedUser();
        SeedSession(user.Id, ageDays: 400, revokedDaysAgo: 300);

        var result = await RetentionSweep.RunAsync(_context, Now);

        Assert.Multiple(() =>
        {
            Assert.That(result.RevokedLoginSessionsDeleted, Is.EqualTo(1));
            Assert.That(result.LoginSessionsScrubbed, Is.Zero,
                "the row was deleted, not scrubbed; counting it as both would overstate what the "
                + "sweep did");
        });
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task Sweep_NeverDeletesAnAuditRow_HoweverOld()
    {
        var user = SeedUser();
        SeedAudit(user.Id, ageDays: 4000);

        await RetentionSweep.RunAsync(_context, Now);

        Assert.That(await _context.IdentityAuditEvents.CountAsync(a => a.UserId == user.Id), Is.EqualTo(1),
            "an eleven-year-old audit row is still the record of what happened to this account");
    }

    [Test]
    public async Task Sweep_DoesNotDeleteAnUnrevokedSession_HoweverOld()
    {
        var user = SeedUser();
        var session = SeedSession(user.Id, ageDays: 4000);

        await RetentionSweep.RunAsync(_context, Now);

        Assert.That(await _context.LoginSessions.AnyAsync(s => s.Id == session.Id), Is.True,
            "only *revoked* sessions are deleted - a live session is a live session");
    }

    [Test]
    public async Task Sweep_DoesNotDeleteARecentlyRevokedSession()
    {
        var user = SeedUser();
        var session = SeedSession(user.Id, ageDays: 400, revokedDaysAgo: 5);

        await RetentionSweep.RunAsync(_context, Now);

        Assert.That(await _context.LoginSessions.AnyAsync(s => s.Id == session.Id), Is.True,
            "a session revoked five days ago is still the answer to 'what did I just cut off', "
            + "however old the login it belonged to was");
    }

    [Test]
    public void RetentionDefaults_AreTheOnesTheSpecNames()
    {
        // A silently wrong default is a retention policy nobody chose.
        Assert.Multiple(() =>
        {
            Assert.That(Env.Retention.LoginSessionIpAndUserAgent, Is.EqualTo(TimeSpan.FromDays(90)));
            Assert.That(Env.Retention.AuditEventIpAddress, Is.EqualTo(TimeSpan.FromDays(180)));
            Assert.That(Env.Retention.RevokedLoginSession, Is.EqualTo(TimeSpan.FromDays(180)));
        });
    }
}
