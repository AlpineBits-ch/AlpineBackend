using System.Security.Claims;
using System.Text;
using Identity.Application.Controllers;
using Identity.Application.Dtos.Request;
using Identity.Application.Services;
using Identity.Contracts.Bus.Events;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Domain.ValueObjects;
using Identity.Infrastructure.Persistence;
using Identity.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using ProtectionLevel = Domain.ProtectionLevel;

namespace Identity.Tests.Endpoints;

/// <summary>
/// The two one-request catastrophes on <see cref="BackupController"/>, and the rules that now stop
/// them.
///
/// <para><b>C1 - <c>rewrap-password</c> destroyed the master key on a bare session token.</b> The
/// route overwrote the wrapping every backup blob on the account is sealed under, took nothing but a
/// token, and then cleared the "your password wrapping is broken" stamp on the way out - so one
/// request permanently orphaned the account's entire encrypted history <i>and reported it as
/// healthy</i>. The loss surfaced at the first restore attempt, potentially months later, with
/// nothing to fall back to.</para>
///
/// <para><b>C2 - per-device isolation was decorative.</b> Every per-device rule compared the
/// <c>X-Device-Id</c> header to the path parameter, i.e. one attacker-controlled string to another.
/// A stolen session could read any of the account's device backups, claim any pending handover, and
/// - worst of the three - have the audit row and the security push both record the device id it had
/// chosen, so the only forensic trail was authored by the exfiltrator. The device is resolved from
/// <c>session_id -&gt; LoginSession.DeviceId</c> now, and the header decides nothing.</para>
///
/// <para>Both are read-shaped as much as write-shaped, which is why the negative cases below are the
/// point of the fixture: a foreign device, an unbound session, a wrong verifier and a replayed
/// ticket each have to be refused for their <i>own</i> reason, or a later change can quietly remove
/// one gate while the tests go on passing on another.</para>
/// </summary>
[TestFixture]
public class BackupControllerTests
{
    private const string UserId = "user-1";
    private const string OwnDeviceId = "device-own";
    private const string OtherDeviceId = "device-other";
    private const string Password = "Correct-Horse-1!";

    /// <summary>Stands in for the client-derived value. Only ever compared, never interpreted - see
    /// <see cref="EncryptedMasterKey.PublicVerifier"/>.</summary>
    private static byte[] Verifier => Enumerable.Repeat((byte)0x77, 32).ToArray();

    private static byte[] WrongVerifier => Enumerable.Repeat((byte)0x33, 32).ToArray();

    private TestIdentityContext _context = null!;
    private FakePasswordVerifier _passwords = null!;
    private FakeIdentityMessageBus _bus = null!;
    private IDistributedCache _cache = null!;
    private MasterKeyRewrapTicketService _tickets = null!;
    private UserManager<ApplicationUser> _users = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestIdentityContext(Guid.NewGuid().ToString());
        _passwords = new FakePasswordVerifier(Password);
        _bus = new FakeIdentityMessageBus();
        _cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        _tickets = new MasterKeyRewrapTicketService(_cache);

        // Only FindByIdAsync is ever reached, but the controller takes the real UserManager, so a
        // real one over the InMemory store is less fiction than a hand-rolled stand-in would be.
        _users = new UserManager<ApplicationUser>(
            new UserStore<ApplicationUser, IdentityRole, MicroserviceContext, string>(_context),
            Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(),
            [],
            [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        _users.Dispose();
        await _context.DisposeAsync();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Fixture plumbing
    // ══════════════════════════════════════════════════════════════════════════

    private BackupController Controller(ClaimsPrincipal principal, HttpContext? http = null)
    {
        var context = http ?? new DefaultHttpContext();
        context.User = principal;

        return new BackupController(_context, _users, _passwords,
            TestSessions.Resolver(_context), _tickets, _bus)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
    }

    private async Task<ApplicationUser> SeedUser(ProtectionLevel level = ProtectionLevel.TrustedSignIn)
    {
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = $"{UserId}-{Guid.NewGuid():N}@test.invalid",
            PhoneNumber = "+10000000000",
            Username = UserId,
            BirthDate = new DateOnly(2000, 1, 1),
        });
        user.Id = UserId;
        user.ProtectionLevel = level;
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    private async Task<UserDevice> SeedDevice(string clientDeviceId, string userId = UserId)
    {
        var device = UserDevice.Create(new CreateUserDeviceParams
        {
            UserId = userId,
            ClientDeviceId = clientDeviceId,
            DeviceName = $"Test {clientDeviceId}",
            DeviceType = DeviceType.Desktop,
            IdentityPublicKey = [1, 2, 3],
        });
        _context.UserDevices.Add(device);
        await _context.SaveChangesAsync();
        return device;
    }

    /// <summary>Seeds the envelope directly. The client-side Argon2 derivation is not under test -
    /// only what the server does with whatever is already stored.</summary>
    private async Task SeedMasterKey(int version = 1, byte[]? verifier = null,
        bool withRecoveryWrapping = true, byte[]? recoveryVerifier = null)
    {
        var user = await _context.Users.FirstAsync(u => u.Id == UserId);
        user.EncryptedMasterKey = new EncryptedMasterKey
        {
            CipherText = [1, 1, 1], Salt = [2], Iv = [3], Version = version, Kdf = "argon2id",
            PublicVerifier = verifier,
        };
        user.RecoveryCodeWrappedMasterKey = withRecoveryWrapping
            ? new EncryptedMasterKey
            {
                CipherText = [4, 4, 4], Salt = [5], Iv = [6], Version = version, Kdf = "argon2id",
                PublicVerifier = recoveryVerifier ?? verifier,
            }
            : null;
        await _context.SaveChangesAsync();
    }

    private async Task<UserDeviceBackup> SeedBackup(UserDevice device, int version = 1,
        int recoveryKeyVersion = 1, DateTimeOffset? at = null)
    {
        var blob = UserDeviceBackup.Create(new CreateUserDeviceBackupParams
        {
            UserId = device.UserId,
            DeviceId = device.Id,
            Backup = Encoding.UTF8.GetBytes($"blob-{device.ClientDeviceId}-v{version}"),
            Version = version,
            RecoveryKeyVersion = recoveryKeyVersion,
            // Default well outside MinWriteInterval, so a write test is not accidentally a
            // rate-limit test.
            CreatedAt = at ?? DateTimeOffset.UtcNow.AddHours(-1),
        });
        _context.UserDeviceBackups.Add(blob);
        await _context.SaveChangesAsync();
        return blob;
    }

    /// <summary>An HTTP context whose body and headers are set up for a blob upload.</summary>
    private static DefaultHttpContext UploadContext(byte[] body, int version, int recoveryKeyVersion,
        bool includesEngine = false, string? deviceIdHeader = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(body);
        http.Request.Headers[BackupController.BackupVersionHeader] = version.ToString();
        http.Request.Headers[BackupController.RecoveryKeyVersionHeader] = recoveryKeyVersion.ToString();
        if (includesEngine) http.Request.Headers[BackupController.IncludesEngineHeader] = "true";
        if (deviceIdHeader is not null) http.Request.Headers[BackupController.DeviceIdHeader] = deviceIdHeader;
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static string? ErrorCodeOf(IActionResult result) => result switch
    {
        ObjectResult { Value: { } value } => value.GetType().GetProperty("error")?.GetValue(value) as string,
        _ => null,
    };

    private static int? StatusOf(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode,
        StatusCodeResult s => s.StatusCode,
        _ => null,
    };

    // ══════════════════════════════════════════════════════════════════════════
    // C2 - the device is the session's, not the header's
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The original C2: a session on one device reading another device's blob. The header
    /// is not even involved - it is the path parameter alone, which is what the old code compared
    /// the equally forgeable header against.</summary>
    [Test]
    public async Task GetBackup_ForADeviceThisSessionIsNot_IsRefused()
    {
        await SeedUser();
        var own = await SeedDevice(OwnDeviceId);
        var other = await SeedDevice(OtherDeviceId);
        await SeedBackup(other);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).GetBackup(OtherDeviceId);

        Assert.Multiple(() =>
        {
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status403Forbidden));
            Assert.That(ErrorCodeOf(result), Is.EqualTo("wrong_device"));
        });
    }

    /// <summary>The header is the exact thing C2 was about, so it gets its own case: setting it to
    /// the target device must not buy anything, because nothing reads it for authorization.</summary>
    [Test]
    public async Task GetBackup_WithTheHeaderForgedToTheTargetDevice_IsStillRefused()
    {
        await SeedUser();
        var own = await SeedDevice(OwnDeviceId);
        var other = await SeedDevice(OtherDeviceId);
        await SeedBackup(other);

        var http = new DefaultHttpContext();
        http.Request.Headers[BackupController.DeviceIdHeader] = OtherDeviceId;

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal, http).GetBackup(OtherDeviceId);

        Assert.That(ErrorCodeOf(result), Is.EqualTo("wrong_device"));
    }

    [Test]
    public async Task GetBackup_FromTheOwningDevice_ReturnsTheBlob()
    {
        await SeedUser();
        await SeedMasterKey();
        var own = await SeedDevice(OwnDeviceId);
        var blob = await SeedBackup(own);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).GetBackup(OwnDeviceId);

        Assert.That(result, Is.TypeOf<FileContentResult>());
        Assert.That(((FileContentResult)result).FileContents, Is.EqualTo(blob.Backup));
    }

    /// <summary>The audit row and the push are the only durable trace of an exfiltration, and the old
    /// code wrote whatever the caller put in the header into both - so the record of the theft was
    /// authored by the thief. Nothing here comes off the wire.</summary>
    [Test]
    public async Task GetBackup_RecordsTheSessionDevice_NotTheHeader()
    {
        await SeedUser();
        await SeedMasterKey();
        var own = await SeedDevice(OwnDeviceId);
        await SeedBackup(own);

        var http = new DefaultHttpContext();
        http.Request.Headers[BackupController.DeviceIdHeader] = "totally-made-up-device";

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        await Controller(principal, http).GetBackup(OwnDeviceId);

        var audit = await _context.IdentityAuditEvents
            .SingleAsync(e => e.Action == IdentityAuditActions.BackupRead);
        var push = _bus.Published.OfType<DeviceBackupRead>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(audit.ClientDeviceId, Is.EqualTo(OwnDeviceId));
            Assert.That(push.ReadByDeviceId, Is.EqualTo(OwnDeviceId));
        });
    }

    /// <summary>Writes are bound too. Retention keeps three versions, so three writes to somebody
    /// else's blob destroy the real one - being a write does not make it the harmless direction.</summary>
    [Test]
    public async Task PutBackup_ForADeviceThisSessionIsNot_IsRefused()
    {
        await SeedUser();
        await SeedMasterKey();
        var own = await SeedDevice(OwnDeviceId);
        var other = await SeedDevice(OtherDeviceId);
        await SeedBackup(other);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var http = UploadContext("junk"u8.ToArray(), version: 9, recoveryKeyVersion: 1);

        var result = await Controller(principal, http).PutBackup(OtherDeviceId);

        Assert.That(ErrorCodeOf(result), Is.EqualTo("wrong_device"));
        Assert.That(await _context.UserDeviceBackups.CountAsync(b => b.DeviceId == other.Id), Is.EqualTo(1));
    }

    [Test]
    public async Task DeleteBackup_ForADeviceThisSessionIsNot_IsRefused()
    {
        await SeedUser();
        var own = await SeedDevice(OwnDeviceId);
        var other = await SeedDevice(OtherDeviceId);
        await SeedBackup(other);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).DeleteBackup(OtherDeviceId);

        Assert.That(ErrorCodeOf(result), Is.EqualTo("wrong_device"));
        Assert.That(await _context.UserDeviceBackups.AnyAsync(b => b.DeviceId == other.Id), Is.True);
    }

    /// <summary>Metadata is not key material, but it is still per-device: if one route shaped
    /// <c>devices/client/{id}/...</c> ignores the rule, that is the one an attacker uses.</summary>
    [Test]
    public async Task GetBackupMeta_ForADeviceThisSessionIsNot_IsRefused()
    {
        await SeedUser();
        var own = await SeedDevice(OwnDeviceId);
        var other = await SeedDevice(OtherDeviceId);
        await SeedBackup(other);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);

        Assert.That(ErrorCodeOf(await Controller(principal).GetBackupMeta(OtherDeviceId)),
            Is.EqualTo("wrong_device"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // C2 - the unbound session, and the way back out of it
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Every session issued before <c>/connect/token</c> recorded a device is in this state,
    /// so the refusal has to be distinguishable from "you are a different device" and has to name a
    /// remedy - otherwise the deploy takes these routes away from real users with no way back.</summary>
    [Test]
    public async Task GetBackup_FromASessionBoundToNoDevice_IsRefusedWithARemedy()
    {
        await SeedUser();
        var own = await SeedDevice(OwnDeviceId);
        await SeedBackup(own);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, deviceRowId: null);
        var result = await Controller(principal).GetBackup(OwnDeviceId);

        Assert.Multiple(() =>
        {
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status403Forbidden));
            Assert.That(ErrorCodeOf(result), Is.EqualTo("session_device_unbound"));
        });

        var value = ((ObjectResult)result).Value!;
        var remedy = value.GetType().GetProperty("remedy")?.GetValue(value) as string;

        Assert.That(remedy, Does.Contain("bind-session").And.Contain(OwnDeviceId),
            "A bare 403 leaves a client that worked yesterday with nothing to act on.");
    }

    /// <summary>A token with no <c>session_id</c> claim at all - the shape of an access token minted
    /// before session tracking. It resolves to no device, which must fail closed rather than being
    /// mistaken for "no rule applies".</summary>
    [Test]
    public async Task GetBackup_FromATokenWithNoSessionClaim_IsRefused()
    {
        await SeedUser();
        var own = await SeedDevice(OwnDeviceId);
        await SeedBackup(own);

        var result = await Controller(TestPrincipal.ForUser(UserId)).GetBackup(OwnDeviceId);

        Assert.That(ErrorCodeOf(result), Is.EqualTo("session_device_unbound"));
    }

    /// <summary>A revoked session must not keep acting as its device. The revocation is the whole
    /// point of the session table.</summary>
    [Test]
    public async Task GetBackup_FromARevokedSession_IsRefused()
    {
        await SeedUser();
        var own = await SeedDevice(OwnDeviceId);
        await SeedBackup(own);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var session = await _context.LoginSessions.SingleAsync();
        session.Revoke();
        await _context.SaveChangesAsync();

        Assert.That(ErrorCodeOf(await Controller(principal).GetBackup(OwnDeviceId)),
            Is.EqualTo("session_device_unbound"));
    }

    /// <summary>Not a 403: <c>ClientDeviceId</c> is only unique per user, so "signed in as the wrong
    /// account" is a real and distinguishable case, and confirming a device exists on some other
    /// account would leak more than the mistake is worth.</summary>
    [Test]
    public async Task GetBackup_ForAnotherAccountsDevice_Is403WithoutConfirmingWhoseItIs()
    {
        await SeedUser();
        var own = await SeedDevice(OwnDeviceId);
        await SeedDevice("shared-id", userId: "someone-else");

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).GetBackup("shared-id");

        Assert.That(result, Is.TypeOf<ForbidResult>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // C2 - transfers, which are a copy of a signing key
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The claim hands over a wrapped signing key. The old code took the target device from
    /// the header, so any session could claim any of the account's pending handovers.</summary>
    [Test]
    public async Task ClaimTransfer_ByADeviceItIsNotAddressedTo_IsNotFound()
    {
        await SeedUser();
        var source = await SeedDevice("device-source");
        var intended = await SeedDevice(OtherDeviceId);
        var eavesdropper = await SeedDevice(OwnDeviceId);

        var transfer = UserBackupTransfer.Create(new CreateUserBackupTransferParams
        {
            UserId = UserId,
            SourceDeviceId = source.ClientDeviceId,
            TargetDeviceId = intended.ClientDeviceId,
            WrappedTo = [9],
            CipherText = [8, 8, 8],
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        _context.UserBackupTransfers.Add(transfer);
        await _context.SaveChangesAsync();

        var http = new DefaultHttpContext();
        http.Request.Headers[BackupController.DeviceIdHeader] = intended.ClientDeviceId;

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, eavesdropper.Id);
        var result = await Controller(principal, http).ClaimTransfer(transfer.Id);

        // Not-found rather than forbidden: confirming one exists tells an attacker which of the
        // user's devices are mid-handover.
        Assert.That(result, Is.TypeOf<NotFoundResult>());
        Assert.That(await _context.UserBackupTransfers.AnyAsync(t => t.Id == transfer.Id), Is.True,
            "A refused claim must not consume the transfer - that would be a denial of service on "
            + "the device the handover was actually for.");
    }

    /// <summary>Single use is enforced by deleting the row, not by a flag: a row that survives a
    /// claim is a spare copy of a signing key sitting in a database nobody is watching.</summary>
    [Test]
    public async Task ClaimTransfer_TwiceFromTheRightDevice_OnlySucceedsOnce()
    {
        await SeedUser();
        var source = await SeedDevice("device-source");
        var target = await SeedDevice(OwnDeviceId);

        var transfer = UserBackupTransfer.Create(new CreateUserBackupTransferParams
        {
            UserId = UserId,
            SourceDeviceId = source.ClientDeviceId,
            TargetDeviceId = target.ClientDeviceId,
            WrappedTo = [9],
            CipherText = [8, 8, 8],
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        _context.UserBackupTransfers.Add(transfer);
        await _context.SaveChangesAsync();

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, target.Id);

        var first = await Controller(principal).ClaimTransfer(transfer.Id);
        var second = await Controller(principal).ClaimTransfer(transfer.Id);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.TypeOf<OkObjectResult>());
            Assert.That(second, Is.TypeOf<NotFoundResult>());
        });
    }

    /// <summary>The source is the session's device, never a header. Letting the caller name which of
    /// the account's devices a signing key is coming from is the same mistake as letting it name
    /// which backup it may read.</summary>
    [Test]
    public async Task CreateTransfer_FromAnUnboundSession_IsRefused()
    {
        await SeedUser();
        await SeedDevice(OwnDeviceId);
        var target = await SeedDevice(OtherDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, deviceRowId: null);
        var result = await Controller(principal).CreateTransfer(new CreateBackupTransferDto
        {
            TargetDeviceId = target.ClientDeviceId,
            WrappedTo = [1],
            CipherText = [2, 2],
        });

        Assert.That(ErrorCodeOf(result), Is.EqualTo("session_device_unbound"));
        Assert.That(await _context.UserBackupTransfers.AnyAsync(), Is.False);
    }

    /// <summary>A transfer is a copy of a signing key; the only reason the server can be trusted to
    /// carry one is that it never crosses an account boundary.</summary>
    [Test]
    public async Task CreateTransfer_ToAnotherAccountsDevice_IsNotFound()
    {
        await SeedUser();
        var own = await SeedDevice(OwnDeviceId);
        await SeedDevice("stranger-device", userId: "someone-else");

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).CreateTransfer(new CreateBackupTransferDto
        {
            TargetDeviceId = "stranger-device",
            WrappedTo = [1],
            CipherText = [2, 2],
        });

        Assert.That(result, Is.TypeOf<NotFoundObjectResult>());
        Assert.That(await _context.UserBackupTransfers.AnyAsync(), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // C1 - the credential gate on rewrap-password
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>C1 itself. This exact request used to succeed and orphan every blob on the
    /// account.</summary>
    [Test]
    public async Task RewrapPassword_WithNothingButASessionToken_IsRefused()
    {
        await SeedUser();
        await SeedMasterKey(verifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).RewrapPassword(new RewrapMasterKeyDto
        {
            Version = 1,
            PasswordWrapping = Wrapping(Verifier),
        });

        Assert.Multiple(() =>
        {
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status403Forbidden));
            Assert.That(ErrorCodeOf(result), Is.EqualTo("credential_required"));
        });

        var user = await _context.Users.AsNoTracking().FirstAsync(u => u.Id == UserId);
        Assert.That(user.EncryptedMasterKey!.CipherText, Is.EqualTo(new byte[] { 1, 1, 1 }),
            "The stored wrapping must be untouched - a refusal that half-writes is C1 again.");
    }

    /// <summary>The in-app journey: the user changed their password while still signed in, so the
    /// password is available and is the credential.</summary>
    [Test]
    public async Task RewrapPassword_WithTheAccountPassword_Succeeds()
    {
        await SeedUser();
        await SeedMasterKey(verifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);
        await MarkPasswordWrappingStale();

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).RewrapPassword(new RewrapMasterKeyDto
        {
            Version = 1,
            Password = Password,
            PasswordWrapping = Wrapping(Verifier),
        });

        Assert.That(result, Is.TypeOf<OkObjectResult>());
        Assert.That(_passwords.Calls, Is.EqualTo(1), "The gate must actually reach the password check.");

        var user = await _context.Users.AsNoTracking().FirstAsync(u => u.Id == UserId);
        Assert.Multiple(() =>
        {
            Assert.That(user.EncryptedMasterKey!.CipherText, Is.EqualTo(new byte[] { 7, 7, 7 }));
            Assert.That(user.MasterKeyPasswordWrappingInvalidatedAt, Is.Null);
            // Not rotation. Every blob sealed under v1 stays readable, which is the entire reason
            // this route exists rather than a version bump.
            Assert.That(user.EncryptedMasterKey.Version, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RewrapPassword_WithTheWrongPassword_IsRefused()
    {
        await SeedUser();
        await SeedMasterKey(verifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).RewrapPassword(new RewrapMasterKeyDto
        {
            Version = 1,
            Password = "not-the-password",
            PasswordWrapping = Wrapping(Verifier),
        });

        Assert.That(ErrorCodeOf(result), Is.EqualTo("credential_required"));
    }

    /// <summary>The recovery journey: the password was just reset, so by definition there is no usable
    /// one - but the caller proved control of the account's email a moment ago, and the ticket
    /// carries that proof forward.</summary>
    [Test]
    public async Task RewrapPassword_WithAResetTicket_Succeeds()
    {
        await SeedUser();
        await SeedMasterKey(verifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);
        await MarkPasswordWrappingStale();

        var ticket = await _tickets.IssueAsync(UserId);
        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);

        var result = await Controller(principal).RewrapPassword(new RewrapMasterKeyDto
        {
            Version = 1,
            RewrapTicket = ticket,
            PasswordWrapping = Wrapping(Verifier),
        });

        Assert.That(result, Is.TypeOf<OkObjectResult>());
        Assert.That(_passwords.Calls, Is.Zero,
            "A valid ticket is the credential on this path; demanding the password too would make "
            + "the recovery journey - the only one that needs this route - impossible.");
    }

    /// <summary>Single use. A ticket that can be replayed is a session token with extra steps, which
    /// is exactly what C1 was.</summary>
    [Test]
    public async Task RewrapPassword_WithAReplayedTicket_IsRefused()
    {
        await SeedUser();
        await SeedMasterKey(verifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);

        var ticket = await _tickets.IssueAsync(UserId);
        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);

        var first = await Controller(principal).RewrapPassword(new RewrapMasterKeyDto
        {
            Version = 1, RewrapTicket = ticket, PasswordWrapping = Wrapping(Verifier),
        });

        var replay = await Controller(principal).RewrapPassword(new RewrapMasterKeyDto
        {
            Version = 1, RewrapTicket = ticket, PasswordWrapping = Wrapping(Verifier, cipher: [9, 9, 9]),
        });

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.TypeOf<OkObjectResult>());
            Assert.That(ErrorCodeOf(replay), Is.EqualTo("credential_required"));
        });

        var user = await _context.Users.AsNoTracking().FirstAsync(u => u.Id == UserId);
        Assert.That(user.EncryptedMasterKey!.CipherText, Is.EqualTo(new byte[] { 7, 7, 7 }),
            "The replay must not have overwritten what the first, legitimate call wrote.");
    }

    /// <summary>A wrong guess must not invalidate the real ticket, or guessing becomes a denial of
    /// service on the one route the recovery journey depends on.</summary>
    [Test]
    public async Task RewrapPassword_WithAWrongTicket_LeavesTheRealOneUsable()
    {
        await SeedUser();
        await SeedMasterKey(verifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);

        var ticket = await _tickets.IssueAsync(UserId);
        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);

        var guess = await Controller(principal).RewrapPassword(new RewrapMasterKeyDto
        {
            Version = 1, RewrapTicket = "0123456789abcdef", PasswordWrapping = Wrapping(Verifier),
        });

        var real = await Controller(principal).RewrapPassword(new RewrapMasterKeyDto
        {
            Version = 1, RewrapTicket = ticket, PasswordWrapping = Wrapping(Verifier),
        });

        Assert.Multiple(() =>
        {
            Assert.That(ErrorCodeOf(guess), Is.EqualTo("credential_required"));
            Assert.That(real, Is.TypeOf<OkObjectResult>());
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // C1 - the verifier, and what it actually covers
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The case the verifier exists for: a <i>correct</i> credential carrying wrong key
    /// material. Without the comparison this write succeeds and silently orphans every blob while the
    /// account goes on reporting itself healthy.</summary>
    [Test]
    public async Task RewrapPassword_WithACorrectPasswordButAWrongVerifier_IsRefused()
    {
        await SeedUser();
        await SeedMasterKey(verifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).RewrapPassword(new RewrapMasterKeyDto
        {
            Version = 1,
            Password = Password,
            PasswordWrapping = Wrapping(WrongVerifier),
        });

        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());

        var user = await _context.Users.AsNoTracking().FirstAsync(u => u.Id == UserId);
        Assert.That(user.EncryptedMasterKey!.CipherText, Is.EqualTo(new byte[] { 1, 1, 1 }));
    }

    [Test]
    public async Task RewrapPassword_WithNoVerifierAtAll_IsRefused()
    {
        await SeedUser();
        await SeedMasterKey(verifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).RewrapPassword(new RewrapMasterKeyDto
        {
            Version = 1,
            Password = Password,
            PasswordWrapping = Wrapping(verifier: null),
        });

        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
    }

    /// <summary>The verifier is compared against the <i>recovery-code</i> wrapping's copy in
    /// preference to the password one - on the journey this route exists for, that is the wrapping
    /// the client just opened and therefore the only one whose verifier it can reproduce.</summary>
    [Test]
    public async Task RewrapPassword_ComparesAgainstTheRecoveryCodeWrappingsVerifier()
    {
        await SeedUser();
        // The two disagree, which only happens on an account mid-retrofit. The recovery-code copy is
        // the one the client can reproduce, so it is the one that must be honoured.
        await SeedMasterKey(verifier: WrongVerifier, recoveryVerifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).RewrapPassword(new RewrapMasterKeyDto
        {
            Version = 1, Password = Password, PasswordWrapping = Wrapping(Verifier),
        });

        Assert.That(result, Is.TypeOf<OkObjectResult>());
    }

    /// <summary>
    /// The honesty case, and the one that made §L.8 worth reopening.
    ///
    /// <para>No account in the field has a verifier, so on those accounts there is nothing to compare
    /// and the credential is the only gate that bites. Refusing them instead would brick the recovery
    /// journey for the entire install base to enforce a check whose input does not exist. The write is
    /// allowed, the response says so, and the submitted value is stored so the <i>next</i> one is
    /// checked for real.</para>
    /// </summary>
    [Test]
    public async Task RewrapPassword_OnAnAccountWithNoStoredVerifier_SucceedsButReportsItWasNotChecked()
    {
        await SeedUser();
        await SeedMasterKey(verifier: null, withRecoveryWrapping: true, recoveryVerifier: null);
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).RewrapPassword(new RewrapMasterKeyDto
        {
            Version = 1, Password = Password, PasswordWrapping = Wrapping(Verifier),
        });

        Assert.That(result, Is.TypeOf<OkObjectResult>());

        var value = ((OkObjectResult)result).Value!;
        Assert.That(value.GetType().GetProperty("verifierChecked")?.GetValue(value), Is.False,
            "Reporting a proof that was not performed is how the field became decorative in the "
            + "first place.");

        var audit = await _context.IdentityAuditEvents
            .SingleAsync(e => e.Action == IdentityAuditActions.MasterKeyRewrapped);
        Assert.That(audit.Detail, Does.Contain("unverified"));
    }

    /// <summary>Trust on first use, so the gap above closes itself. The second re-wrap on the same
    /// account is a real comparison.</summary>
    [Test]
    public async Task RewrapPassword_StoresTheVerifierSoTheNextRewrapIsChecked()
    {
        await SeedUser();
        await SeedMasterKey(verifier: null, withRecoveryWrapping: true, recoveryVerifier: null);
        var own = await SeedDevice(OwnDeviceId);
        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);

        await Controller(principal).RewrapPassword(new RewrapMasterKeyDto
        {
            Version = 1, Password = Password, PasswordWrapping = Wrapping(Verifier),
        });

        var second = await Controller(principal).RewrapPassword(new RewrapMasterKeyDto
        {
            Version = 1, Password = Password, PasswordWrapping = Wrapping(WrongVerifier, cipher: [5, 5, 5]),
        });

        Assert.That(second, Is.TypeOf<BadRequestObjectResult>());

        var user = await _context.Users.AsNoTracking().FirstAsync(u => u.Id == UserId);
        Assert.That(user.EncryptedMasterKey!.CipherText, Is.EqualTo(new byte[] { 7, 7, 7 }));
    }

    /// <summary>A version mismatch means the client holds a master key the account has moved on from;
    /// writing it would make every blob at the current version unopenable.</summary>
    [Test]
    public async Task RewrapPassword_AtTheWrongVersion_IsRefusedAfterTheOtherGatesPass()
    {
        await SeedUser();
        await SeedMasterKey(version: 3, verifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).RewrapPassword(new RewrapMasterKeyDto
        {
            Version = 7, Password = Password, PasswordWrapping = Wrapping(Verifier),
        });

        Assert.That(result, Is.TypeOf<ConflictObjectResult>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The envelope: where the verifier comes from
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Establishing key material is the only moment the verifier can be obtained, so it is
    /// the one place it can be demanded. Without this the field stays null forever and the comparison
    /// above never happens on any account.</summary>
    [Test]
    public async Task PutRecoveryKey_RotatingWithoutAVerifier_IsRefused()
    {
        var user = await SeedUser();
        await SeedMasterKey(version: 1, verifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).PutRecoveryKey(new PutRecoveryKeyDto
        {
            Version = 2, Password = Password, CipherText = [9, 9], Salt = [1], Iv = [2],
            PublicVerifier = null,
        }, acknowledgeOrphans: true);

        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());

        await _context.Entry(user).ReloadAsync();
        Assert.That(user.EncryptedMasterKey!.Version, Is.EqualTo(1));
    }

    /// <summary>The retrofit path for the install base: the same key, at the same version, gaining
    /// the value derived from it. Demanding a rotation instead would orphan every blob on the account
    /// in order to turn a check on.</summary>
    [Test]
    public async Task PutRecoveryKey_AtTheSameVersion_BackfillsTheVerifierWithoutRotating()
    {
        await SeedUser();
        await SeedMasterKey(version: 1, verifier: null, withRecoveryWrapping: false);
        var own = await SeedDevice(OwnDeviceId);
        await SeedBackup(own, version: 1, recoveryKeyVersion: 1);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).PutRecoveryKey(new PutRecoveryKeyDto
        {
            Version = 1, Password = Password, CipherText = [1, 1, 1], Salt = [2], Iv = [3],
            PublicVerifier = Verifier,
        });

        Assert.That(result, Is.TypeOf<OkObjectResult>());
        Assert.That(((PutRecoveryKeyResultDto)((OkObjectResult)result).Value!).HasPublicVerifier, Is.True);

        var user = await _context.Users.AsNoTracking().FirstAsync(u => u.Id == UserId);
        Assert.Multiple(() =>
        {
            Assert.That(user.EncryptedMasterKey!.PublicVerifier, Is.EqualTo(Verifier));
            // The version did not move, so nothing was orphaned - which is the whole reason this is
            // the retrofit path rather than a rotation.
            Assert.That(user.EncryptedMasterKey.Version, Is.EqualTo(1));
        });
        Assert.That(await _context.UserDeviceBackups.CountAsync(), Is.EqualTo(1));
    }

    /// <summary>Once stored it is immutable at a version. A verifier a later request can overwrite is
    /// not a check - it is a field an attacker sets to whatever their own wrapping produces.</summary>
    [Test]
    public async Task PutRecoveryKey_AtTheSameVersion_WillNotOverwriteAStoredVerifier()
    {
        await SeedUser();
        await SeedMasterKey(version: 1, verifier: Verifier, withRecoveryWrapping: false);
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).PutRecoveryKey(new PutRecoveryKeyDto
        {
            Version = 1, Password = Password, CipherText = [1, 1, 1], Salt = [2], Iv = [3],
            PublicVerifier = WrongVerifier,
        });

        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());

        var user = await _context.Users.AsNoTracking().FirstAsync(u => u.Id == UserId);
        Assert.That(user.EncryptedMasterKey!.PublicVerifier, Is.EqualTo(Verifier));
    }

    /// <summary>§L.8's other half. Both wrappings seal the same key, so both derive the same verifier;
    /// two different values mean the recovery code opens key material no backup is sealed under - a
    /// loss discovered years later, on the one journey that has no fallback.</summary>
    [Test]
    public async Task PutRecoveryKey_WithMismatchedVerifiersAcrossTheTwoWrappings_IsRefused()
    {
        var user = await SeedUser();
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).PutRecoveryKey(new PutRecoveryKeyDto
        {
            Version = 1, Password = Password, CipherText = [1, 1, 1], Salt = [2], Iv = [3],
            PublicVerifier = Verifier,
            RecoveryCodeWrapping = Wrapping(WrongVerifier, cipher: [4, 4, 4]),
        });

        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());

        await _context.Entry(user).ReloadAsync();
        Assert.That(user.EncryptedMasterKey, Is.Null);
    }

    /// <summary>The recovery-code wrapping inherits the envelope's verifier rather than being allowed
    /// to leave it blank, or the preference for the recovery copy in <c>rewrap-password</c> would keep
    /// selecting the weaker source.</summary>
    [Test]
    public async Task PutRecoveryKey_AddingARecoveryWrapping_InheritsTheStoredVerifier()
    {
        await SeedUser();
        await SeedMasterKey(version: 1, verifier: Verifier, withRecoveryWrapping: false);
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        await Controller(principal).PutRecoveryKey(new PutRecoveryKeyDto
        {
            Version = 1, Password = Password, CipherText = [1, 1, 1], Salt = [2], Iv = [3],
            RecoveryCodeWrapping = Wrapping(verifier: null, cipher: [4, 4, 4]),
        });

        var user = await _context.Users.AsNoTracking().FirstAsync(u => u.Id == UserId);
        Assert.That(user.RecoveryCodeWrappedMasterKey!.PublicVerifier, Is.EqualTo(Verifier));
    }

    /// <summary>The envelope is what makes every backup readable, so writing it costs the account
    /// password rather than merely holding a token for it.</summary>
    [Test]
    public async Task PutRecoveryKey_WithoutThePassword_IsRefused()
    {
        var user = await SeedUser();
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).PutRecoveryKey(new PutRecoveryKeyDto
        {
            Version = 1, Password = "wrong", CipherText = [1], Salt = [2], Iv = [3],
            PublicVerifier = Verifier,
        });

        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());

        await _context.Entry(user).ReloadAsync();
        Assert.That(user.EncryptedMasterKey, Is.Null);
    }

    /// <summary>A rotation orphans every blob sealed under the old version, so it is refused until the
    /// client has been told exactly which devices it is about to lose.</summary>
    [Test]
    public async Task PutRecoveryKey_RotatingWithOrphans_RefusesUntilAcknowledged()
    {
        await SeedUser();
        await SeedMasterKey(version: 1, verifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);
        await SeedBackup(own, version: 1, recoveryKeyVersion: 1);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var dto = new PutRecoveryKeyDto
        {
            Version = 2, Password = Password, CipherText = [9], Salt = [2], Iv = [3],
            PublicVerifier = WrongVerifier,
        };

        var refused = await Controller(principal).PutRecoveryKey(dto);

        Assert.That(refused, Is.TypeOf<ConflictObjectResult>());
        Assert.That(((PutRecoveryKeyResultDto)((ConflictObjectResult)refused).Value!).OrphanedBlobDeviceIds,
            Is.EquivalentTo(new[] { OwnDeviceId }));

        var accepted = await Controller(principal).PutRecoveryKey(dto, acknowledgeOrphans: true);
        Assert.That(accepted, Is.TypeOf<OkObjectResult>());

        var user = await _context.Users.AsNoTracking().FirstAsync(u => u.Id == UserId);
        Assert.That(user.EncryptedMasterKey!.Version, Is.EqualTo(2));
    }

    /// <summary>A lower version is not a rotation, it is a client holding stale state. Accepting it
    /// would silently replace the current key with an older one.</summary>
    [Test]
    public async Task PutRecoveryKey_GoingBackwards_IsRefused()
    {
        await SeedUser();
        await SeedMasterKey(version: 5, verifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).PutRecoveryKey(new PutRecoveryKeyDto
        {
            Version = 2, Password = Password, CipherText = [9], Salt = [2], Iv = [3],
            PublicVerifier = Verifier,
        });

        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
    }

    /// <summary>Different bytes under an unchanged version means either a re-wrap under a new password
    /// - which <c>rewrap-password</c> exists for and this route cannot distinguish - or a different
    /// master key masquerading as the same one, which would make every blob at this version
    /// unopenable while claiming nothing changed.</summary>
    [Test]
    public async Task PutRecoveryKey_AtTheSameVersionWithDifferentCipherText_IsRefused()
    {
        await SeedUser();
        await SeedMasterKey(version: 1, verifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).PutRecoveryKey(new PutRecoveryKeyDto
        {
            Version = 1, Password = Password, CipherText = [2, 2, 2], Salt = [2], Iv = [3],
            PublicVerifier = Verifier,
        });

        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
    }

    /// <summary>Unbounded KDF parameters are a denial of service on the recovery path, which is the
    /// one journey with no fallback. Weak ones are not the risk - those still fail closed.</summary>
    [Test]
    public async Task PutRecoveryKey_WithAbsurdKdfParameters_IsRefused()
    {
        await SeedUser();
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var result = await Controller(principal).PutRecoveryKey(new PutRecoveryKeyDto
        {
            Version = 1, Password = Password, CipherText = [1], Salt = [2], Iv = [3],
            MemoryKiB = 4 * 1024 * 1024, PublicVerifier = Verifier,
        });

        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Blob upload rules
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>A blob sealed under an envelope the account no longer has is unopenable the moment it
    /// is written. Refusing is the difference between an error the client can act on and a restore
    /// that fails months later with nothing left to fall back to.</summary>
    [Test]
    public async Task PutBackup_UnderASupersededRecoveryKeyVersion_IsRefused()
    {
        await SeedUser();
        await SeedMasterKey(version: 4, verifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var http = UploadContext("state"u8.ToArray(), version: 1, recoveryKeyVersion: 3);

        var result = await Controller(principal, http).PutBackup(OwnDeviceId);

        Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status412PreconditionFailed));
        Assert.That(await _context.UserDeviceBackups.AnyAsync(), Is.False);
    }

    /// <summary>Retention keeps three versions total, counting the incoming row - otherwise the
    /// window is N plus whatever is being written and a hostile client can still push the good blob
    /// out.</summary>
    [Test]
    public async Task PutBackup_KeepsOnlyTheRetainedVersions()
    {
        await SeedUser();
        await SeedMasterKey(version: 1, verifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);

        for (var v = 1; v <= UserDeviceBackup.RetainedVersions; v++)
        {
            await SeedBackup(own, version: v, recoveryKeyVersion: 1,
                at: DateTimeOffset.UtcNow.AddHours(-10 + v));
        }

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var http = UploadContext("newest"u8.ToArray(), version: 99, recoveryKeyVersion: 1);

        var result = await Controller(principal, http).PutBackup(OwnDeviceId);

        Assert.That(result, Is.TypeOf<OkObjectResult>());

        var kept = await _context.UserDeviceBackups.AsNoTracking()
            .Where(b => b.DeviceId == own.Id).Select(b => b.Version).ToListAsync();

        Assert.That(kept, Has.Count.EqualTo(UserDeviceBackup.RetainedVersions));
        Assert.That(kept, Does.Contain(99));
        Assert.That(kept, Does.Not.Contain(1), "The oldest restore point is the one that goes.");
    }

    /// <summary>Under <c>VerifiedDevices</c> the tier's whole promise is that a server-assisted
    /// password reset cannot restore encrypted history. Uploading the provider store when the password
    /// is the only credential that opens the master key puts every group's ratchet material one reset
    /// away.</summary>
    [Test]
    public async Task PutBackup_WithEngineState_OnVerifiedDevicesWithNoRecoveryWrapping_IsRefused()
    {
        await SeedUser(ProtectionLevel.VerifiedDevices);
        await SeedMasterKey(version: 1, verifier: Verifier, withRecoveryWrapping: false);
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var http = UploadContext("engine"u8.ToArray(), version: 1, recoveryKeyVersion: 1,
            includesEngine: true);

        var result = await Controller(principal, http).PutBackup(OwnDeviceId);

        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
        Assert.That(await _context.UserDeviceBackups.AnyAsync(), Is.False);
    }

    [Test]
    public async Task PutBackup_WithEngineState_OnVerifiedDevicesWithARecoveryWrapping_IsAllowed()
    {
        await SeedUser(ProtectionLevel.VerifiedDevices);
        await SeedMasterKey(version: 1, verifier: Verifier, withRecoveryWrapping: true);
        var own = await SeedDevice(OwnDeviceId);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var http = UploadContext("engine"u8.ToArray(), version: 1, recoveryKeyVersion: 1,
            includesEngine: true);

        Assert.That(await Controller(principal, http).PutBackup(OwnDeviceId), Is.TypeOf<OkObjectResult>());
    }

    /// <summary>Two sessions of the same device backing up concurrently would otherwise silently drop
    /// one side's state.</summary>
    [Test]
    public async Task PutBackup_WithAStaleIfMatch_IsAConflict()
    {
        await SeedUser();
        await SeedMasterKey(version: 1, verifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);
        await SeedBackup(own, version: 1, recoveryKeyVersion: 1);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var http = UploadContext("next"u8.ToArray(), version: 2, recoveryKeyVersion: 1);
        http.Request.Headers.IfMatch = "\"not-the-current-etag\"";

        Assert.That(await Controller(principal, http).PutBackup(OwnDeviceId),
            Is.TypeOf<ConflictObjectResult>());
    }

    /// <summary>H8: the write is bound to the device, so retention cannot be used as a delete. This
    /// pins the rate limit specifically, because it is what stops three rapid writes from rolling the
    /// window even when they are the device's own.</summary>
    [Test]
    public async Task PutBackup_InsideTheMinimumWriteInterval_IsRateLimited()
    {
        await SeedUser();
        await SeedMasterKey(version: 1, verifier: Verifier);
        var own = await SeedDevice(OwnDeviceId);
        await SeedBackup(own, version: 1, recoveryKeyVersion: 1, at: DateTimeOffset.UtcNow);

        var principal = await TestSessions.SignedInOnAsync(_context, UserId, own.Id);
        var http = UploadContext("next"u8.ToArray(), version: 2, recoveryKeyVersion: 1);

        var result = await Controller(principal, http).PutBackup(OwnDeviceId);

        Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status429TooManyRequests));
        Assert.That(http.Response.Headers.RetryAfter.ToString(), Is.Not.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════

    private static MasterKeyWrappingDto Wrapping(byte[]? verifier, byte[]? cipher = null) => new()
    {
        CipherText = cipher ?? [7, 7, 7],
        Salt = [8],
        Iv = [9],
        Kdf = "argon2id",
        Iterations = 3,
        MemoryKiB = 65536,
        Parallelism = 1,
        PublicVerifier = verifier,
    };

    private async Task MarkPasswordWrappingStale()
    {
        var user = await _context.Users.FirstAsync(u => u.Id == UserId);
        user.MasterKeyPasswordWrappingInvalidatedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();
    }
}
