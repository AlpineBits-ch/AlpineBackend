using Identity.Domain.Aggregates;
using Microsoft.AspNetCore.Identity;

namespace Identity.Application.Services;

public enum PasswordCheckResult
{
    /// <summary>The caller sent no password at all.</summary>
    NotProvided,

    Ok,

    Incorrect,

    /// <summary>Too many wrong answers.</summary>
    LockedOut,
}

/// <summary>The one way this service checks an account password.</summary>
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

    /// <summary>The refusal text for a failed check.</summary>
    public static string Describe(this PasswordCheckResult result, string action) => result switch
    {
        PasswordCheckResult.LockedOut =>
            $"This account is temporarily locked after too many incorrect passwords. {action} again later.",
        PasswordCheckResult.NotProvided => $"{action} requires the account password.",
        _ => "Incorrect password",
    };
}
