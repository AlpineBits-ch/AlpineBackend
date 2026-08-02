using System.Security.Claims;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Services;

/// <summary>
/// Which registered device the caller's session was established from.
///
/// <para><b><c>X-Device-Id</c> proves nothing.</b> It is a header the caller writes, and every
/// per-device rule that read it was comparing one attacker-controlled string to another. A stolen
/// session could therefore read any of the account's device backups, claim any pending
/// device-to-device transfer, and - worse - have the audit row and the <c>identity.BackupRead</c>
/// push both record the id it had chosen, so the forensic trail was forgeable too.</para>
///
/// <para>The JWT already carries <c>session_id</c>, and <see cref="LoginSession.DeviceId"/> already
/// links a session to the device row it was created from. This performs that join. The header is
/// still accepted on routes where it is only a routing hint (which Welcomes to hand back); it is
/// never accepted where it decides whether the caller may read something.</para>
///
/// <para><b>Binding.</b> A session is normally bound at <c>/connect/token</c>, which reads the
/// device id off the request. The one case that cannot be is a device's very first launch: it has to
/// log in before it can register itself. <see cref="TryClaimAsync"/> closes that gap by letting a
/// still-unbound session claim a device row that nobody has ever been - see that method for the
/// exact rule and for what it deliberately concedes.</para>
///
/// <para><b>The sessions that already exist.</b> Every session created before <c>/connect/token</c>
/// learned to record a device - and every session from a client that sends no device id - has
/// <c>DeviceId == null</c> and therefore loses access to all of the per-device backup and transfer
/// routes the moment this resolver starts gating them. That is a real availability regression on
/// deploy day, and it has exactly three possible answers:</para>
///
/// <list type="bullet">
/// <item><b>Backfill</b> is not available. Nothing on a <c>LoginSession</c> identifies a device
/// except <c>DeviceName</c> and <c>UserAgent</c>, both free text; guessing from them would bind
/// sessions to the wrong handset and hand one device another's backup, which is the bug this class
/// exists to fix, arriving by migration instead of by header.</item>
/// <item><b>A grace window on session age</b> is worse than doing nothing. It would re-open C2 in
/// full for the length of the window, for stolen sessions equally with legitimate ones, and it fails
/// open on the clock - the one property that must never gate a read of key material.</item>
/// <item><b>An explicit re-bind that costs a credential</b> - <see cref="BindExistingAsync"/>. This
/// is the one implemented. It is the same trade <c>/connect/token</c> already makes: the password
/// grant will bind a session to any of the account's registered devices, because a password is proof
/// and a bearer token is not.</item>
/// </list>
/// </summary>
public sealed class SessionDeviceResolver(MicroserviceContext ctx)
{
    public const string SessionIdClaim = "session_id";

    public static string? SessionIdOf(ClaimsPrincipal principal) => principal.FindFirstValue(SessionIdClaim);

    /// <summary>The active device this session was established from, or null when the session is
    /// unbound, revoked, or points at a device that has since been removed.</summary>
    public async Task<UserDevice?> ResolveAsync(ClaimsPrincipal principal, string userId)
    {
        var sessionId = SessionIdOf(principal);
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        var deviceRowId = await ctx.LoginSessions.AsNoTracking()
            .Where(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null)
            .Select(s => s.DeviceId)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(deviceRowId)) return null;

        return await ctx.UserDevices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deviceRowId
                                      && d.UserId == userId
                                      && d.Status == DeviceStatus.Active);
    }

    /// <summary>True when the caller's session is bound to this exact device row.</summary>
    public async Task<bool> IsCallingDeviceAsync(ClaimsPrincipal principal, string userId, string deviceRowId)
    {
        var device = await ResolveAsync(principal, userId);
        return device is not null && string.Equals(device.Id, deviceRowId, StringComparison.Ordinal);
    }

    /// <summary>What <see cref="TryClaimAsync"/> did. The caller needs to distinguish these because
    /// "the session is this device" is an authorization answer, not a diagnostic.</summary>
    public enum ClaimResult
    {
        /// <summary>The session was already bound to this exact row. Nothing changed.</summary>
        AlreadyBound,

        /// <summary>The session is now this device. Staged on the context, not committed.</summary>
        Claimed,

        /// <summary>Left alone: no usable session, the session belongs to a different device, or the
        /// row is not adoptable.</summary>
        Refused,
    }

    /// <summary><see cref="ClaimResult.Claimed"/> and <see cref="ClaimResult.AlreadyBound"/> both mean
    /// "the caller is this device"; only the audit trail cares which.</summary>
    public static bool IsSelf(ClaimResult result) => result is not ClaimResult.Refused;

    /// <summary>
    /// Lets a still-unbound session become a device row, for the first-launch case where the login
    /// necessarily happened before the device existed - and for the installed base, whose sessions
    /// predate device binding entirely and can otherwise never acquire one without the password.
    ///
    /// <para>Stages the change on the context rather than saving, so it commits with the registration
    /// it belongs to.</para>
    ///
    /// <para><b>The rule is "a row nobody has ever been".</b> Claiming a row is what decides whose
    /// backup a session may read, whose backup it may overwrite, and which device-to-device transfers
    /// it may collect - so <see cref="IsAdoptableAsync"/> refuses any row that already has one of
    /// those, or that any session has ever been bound to. A brand-new row satisfies all three
    /// trivially, which is why the first-launch case needs no special casing.</para>
    ///
    /// <para><b>What this still stops.</b> A stolen session token cannot appoint itself an
    /// <i>established</i> device: the laptop whose own session is bound to its row, or that holds an
    /// encrypted backup, or that is one end of a live transfer, is refused here and remains reachable
    /// only through <see cref="BindExistingAsync"/> at the cost of the account password. Nor can a
    /// session that is already some device become another one. That is C2 and §L.7's actual property,
    /// intact.</para>
    ///
    /// <para><b>What this concedes, stated rather than buried.</b> A device row that no session has
    /// ever named and that holds nothing is first-come-first-served among the account's own unbound
    /// sessions - trust on first use. A token stolen before the legitimate handset's next launch can
    /// take that row, which locks the real device out of its own per-device routes until the user
    /// pays the password on <c>bind-session</c>, and makes the thief the recipient of any backup that
    /// device later writes. The window is per row and closes the first time anybody claims it, which
    /// every legitimate launch does. It is accepted because the alternative is worse in exactly the
    /// same direction: without it, an installed-base device cannot re-register at all (its identity
    /// key rotation is gated on being self), so the row stays unclaimed indefinitely and the same
    /// window stays open forever - while the honest user gets a password prompt on every launch.</para>
    /// </summary>
    public async Task<ClaimResult> TryClaimAsync(ClaimsPrincipal principal, string userId, UserDevice device)
    {
        var sessionId = SessionIdOf(principal);
        if (string.IsNullOrWhiteSpace(sessionId)) return ClaimResult.Refused;

        var session = await ctx.LoginSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null);

        if (session is null) return ClaimResult.Refused;

        if (session.DeviceId is not null)
        {
            return string.Equals(session.DeviceId, device.Id, StringComparison.Ordinal)
                ? ClaimResult.AlreadyBound
                : ClaimResult.Refused;
        }

        if (!await IsAdoptableAsync(device)) return ClaimResult.Refused;

        session.DeviceId = device.Id;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        return ClaimResult.Claimed;
    }

    /// <summary>
    /// Whether a row is one nobody has ever been.
    ///
    /// <para>The three checks are not a heuristic for "looks new" - they are an enumeration of
    /// everything being this device grants (see <c>BackupController</c>): reading and writing its
    /// backup, and creating or collecting a transfer at either end. A row with none of them is a row
    /// where adoption transfers no secret that exists yet.</para>
    ///
    /// <para><b>Revoked sessions count.</b> A revoked session is still proof that this row was some
    /// real handset's, which is the question being asked; ignoring them would re-open every row whose
    /// owner had merely signed out.</para>
    /// </summary>
    private async Task<bool> IsAdoptableAsync(UserDevice device)
    {
        // Created by the request that is asking. Nothing can reference it yet, and the queries below
        // would say so - this is here because the first-launch case is the one a reader looks for.
        if (ctx.Entry(device).State == EntityState.Added) return true;

        if (await ctx.LoginSessions.AnyAsync(s => s.DeviceId == device.Id)) return false;
        if (await ctx.UserDeviceBackups.AnyAsync(b => b.DeviceId == device.Id)) return false;

        // Transfers name devices by ClientDeviceId, not by row id.
        return !await ctx.UserBackupTransfers.AnyAsync(
            t => t.UserId == device.UserId
                 && (t.TargetDeviceId == device.ClientDeviceId || t.SourceDeviceId == device.ClientDeviceId));
    }

    /// <summary>The outcome of <see cref="BindExistingAsync"/>. Distinguished because the caller
    /// answers each one differently and "it didn't work" is not enough to act on.</summary>
    public enum BindResult
    {
        /// <summary>Bound. Staged on the context, not committed.</summary>
        Bound,

        /// <summary>Already bound to this same device. Idempotent - a retried request must not look
        /// like a conflict.</summary>
        AlreadyBound,

        /// <summary>Bound to a different device. Refused: a session does not change which handset it
        /// belongs to, and allowing it would make the credential a one-shot purchase of every device
        /// on the account.</summary>
        BoundElsewhere,

        /// <summary>No usable session behind the token - no <c>session_id</c> claim, or the row is
        /// gone or revoked.</summary>
        NoSession,
    }

    /// <summary>
    /// Binds an unbound session to a device row that already exists.
    ///
    /// <para><b>The caller must have verified a credential first.</b> This deliberately does what
    /// <see cref="TryClaimAsync"/> refuses to do - take over a row that is already somebody's - and
    /// the password is the entire reason it is allowed to: without one it would be C2 with extra
    /// steps, a stolen session naming the laptop and then downloading the laptop's backup, which is
    /// precisely the attack this class was written to close.</para>
    ///
    /// <para>Exists because the alternative for the install base is worse. Sessions predating device
    /// binding cannot be repaired by any amount of server-side inference (see the type remarks), so
    /// without this route the only way back is a full sign-out and sign-in - which costs the user the
    /// same password prompt, plus the session, plus every bit of client state hanging off it.</para>
    /// </summary>
    public async Task<BindResult> BindExistingAsync(ClaimsPrincipal principal, string userId, string deviceRowId)
    {
        var sessionId = SessionIdOf(principal);
        if (string.IsNullOrWhiteSpace(sessionId)) return BindResult.NoSession;

        var session = await ctx.LoginSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null);

        if (session is null) return BindResult.NoSession;

        if (session.DeviceId is not null)
        {
            return string.Equals(session.DeviceId, deviceRowId, StringComparison.Ordinal)
                ? BindResult.AlreadyBound
                : BindResult.BoundElsewhere;
        }

        session.DeviceId = deviceRowId;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        return BindResult.Bound;
    }
}
