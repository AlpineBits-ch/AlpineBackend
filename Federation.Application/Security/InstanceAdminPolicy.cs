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
    /// authenticated. The federation admin surface controls which remote instances this server
    /// trusts, and being an Active peer is the only gate on inbound event injection - so a bare
    /// [Authorize] there let any registered user admit themselves as a trusted peer.
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
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return;

        try
        {
            var response = await bus.InvokeAsync<IsUserAdministrativeResponse>(
                new IsUserAdministrativeRequest { UserId = userId });

            if (response.IsAdministrative) context.Succeed(requirement);
        }
        catch (Exception ex)
        {
            // Fail closed: an Identity outage must not turn the federation admin surface into an
            // open one. The requirement simply goes unmet, which yields a 403.
            logger.LogError(ex, "Administrator check failed for user {UserId}; denying access.", userId);
        }
    }
}
