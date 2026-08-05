using System.Security.Claims;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Authorization;
using Wolverine;

namespace Federation.Application.Security;

public static class FederationPolicies
{
    /// <summary>
    /// Requires the caller to be a platform administrator (Identity's UserType.Admin), not merely
    /// authenticated.
    /// </summary>
    public const string InstanceAdmin = "InstanceAdmin";
}

public class InstanceAdminRequirement : IAuthorizationRequirement;

/// <summary>
/// Resolves administrator status from the Identity service over the bus rather than trusting a
/// claim in the token, so revoking an admin takes effect on the next request instead of at token
/// expiry. Only ever evaluated on the federation admin routes, which are low-traffic.
/// </summary>
public class InstanceAdminHandler(IMessageBus bus, ILogger<InstanceAdminHandler> logger)
    : AuthorizationHandler<InstanceAdminRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        InstanceAdminRequirement requirement)
    {
        // `sub` as a fallback: the token carries the user id in both, but which one survives depends
        // on JwtBearerOptions.MapInboundClaims, and a claim-mapping change turning every admin
        // request into a 403 is a failure with no visible cause at all.
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? context.User.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(userId))
        {
            // Reached when the token validated but carries no subject - a differently-issued token,
            // or inbound claim mapping turned off.
            logger.LogWarning(
                "Federation admin request carried no subject claim, so no account could be checked. "
                + "Claims present: {Claims}. Denying access.",
                string.Join(", ", context.User.Claims.Select(c => c.Type)));
            return;
        }

        try
        {
            var response = await bus.InvokeAsync<IsUserAdministrativeResponse>(
                new IsUserAdministrativeRequest { UserId = userId });

            if (response.IsAdministrative)
            {
                context.Succeed(requirement);
                return;
            }

            // The common case, and previously the silent one: the check worked and the answer was
            // no. Without this line a 403 here is indistinguishable from a broker timeout, which is
            // exactly the state this surface was reported to be stuck in - the account simply is not
            // UserType.Admin (a Moderator is not enough: this endpoint decides which remote
            // instances the server trusts).
            logger.LogWarning(
                "Account {UserId} is not a platform administrator (resolved role: {Role}); "
                + "denying the federation admin surface. Set UserType=Admin on that account to allow it.",
                userId, string.IsNullOrEmpty(response.Role) ? "unknown" : response.Role);
        }
        catch (Exception ex)
        {
            // Fail closed: an Identity outage must not turn the federation admin surface into an
            // open one.
            logger.LogError(ex,
                "The administrator check for {UserId} could not be completed - Identity did not "
                + "answer over the bus. Denying access; this is an outage, not a permission decision.",
                userId);
        }
    }
}
