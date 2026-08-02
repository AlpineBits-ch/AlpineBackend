using Identity.Application.Services;
using Identity.Domain.Aggregates;

namespace Identity.Tests.Helpers;

/// <summary>
/// Stands in for <see cref="AccountPasswordVerifier"/>, which is backed by
/// <c>SignInManager.CheckPasswordSignInAsync</c> and cannot be constructed over the InMemory
/// provider.
/// </summary>
internal sealed class FakePasswordVerifier(string correctPassword, bool lockedOut = false)
    : IAccountPasswordVerifier
{
    public int Calls { get; private set; }

    public Task<PasswordCheckResult> CheckAsync(ApplicationUser user, string? password)
    {
        Calls++;

        if (string.IsNullOrEmpty(password)) return Task.FromResult(PasswordCheckResult.NotProvided);
        if (lockedOut) return Task.FromResult(PasswordCheckResult.LockedOut);

        return Task.FromResult(password == correctPassword
            ? PasswordCheckResult.Ok
            : PasswordCheckResult.Incorrect);
    }
}
