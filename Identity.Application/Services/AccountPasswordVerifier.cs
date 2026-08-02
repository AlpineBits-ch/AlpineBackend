using Identity.Domain.Aggregates;
using Microsoft.AspNetCore.Identity;

namespace Identity.Application.Services;

public enum PasswordCheckResult
{
    /// <summary>The caller sent no password at all. Not counted against the lockout budget - a
    /// missing field is a client bug, not a guess.</summary>
    NotProvided,

    Ok,

    Incorrect,

    /// <summary>Too many wrong answers. Distinct from <see cref="Incorrect"/> so the caller can say
    /// "locked" rather than "wrong password", which is the only way a user under attack finds out
    /// somebody else is guessing.</summary>
    LockedOut,
}

/// <summary>
/// The one way this service checks an account password.
///
/// <para><b><c>UserManager.CheckPasswordAsync</c> does not touch the lockout counters.</b> Every
/// password-gated route used it, so the only bound on online guessing was a generic throughput cap -
/// roughly 144k attempts a day, with no lockout, no audit and no notification. That converts a stolen
/// session token into the account password, and several of the routes it guards exist precisely
/// because the password is supposed to be the thing an attacker holding a session does <i>not</i>
/// have.</para>
///
/// <para><see cref="SignInManager{TUser}.CheckPasswordSignInAsync"/> increments
/// <c>AccessFailedCount</c>, honours <c>LockoutEnd</c>, and resets the counter on success. It is the
/// same check; it is the one that keeps score.</para>
/// </summary>
public interface IAccountPasswordVerifier
{
    Task<PasswordCheckResult> CheckAsync(ApplicationUser user, string? password);
}

public sealed class AccountPasswordVerifier(SignInManager<ApplicationUser> signIn) : IAccountPasswordVerifier
{
    public async Task<PasswordCheckResult> CheckAsync(ApplicationUser user, string? password)
    {
        if (string.IsNullOrEmpty(password)) return PasswordCheckResult.NotProvided;

        var result = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);

        if (result.Succeeded) return PasswordCheckResult.Ok;
        return result.IsLockedOut ? PasswordCheckResult.LockedOut : PasswordCheckResult.Incorrect;
    }
}

public static class PasswordCheckResultExtensions
{
    public static bool IsOk(this PasswordCheckResult result) => result == PasswordCheckResult.Ok;

    /// <summary>The refusal text for a failed check. Deliberately says "locked" out loud: an account
    /// being guessed at is exactly what the user needs to be told.</summary>
    public static string Describe(this PasswordCheckResult result, string action) => result switch
    {
        PasswordCheckResult.LockedOut =>
            $"This account is temporarily locked after too many incorrect passwords. {action} again later.",
        PasswordCheckResult.NotProvided => $"{action} requires the account password.",
        _ => "Incorrect password",
    };
}
