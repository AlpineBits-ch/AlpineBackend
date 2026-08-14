using System.Security.Claims;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Authorization;
using Wolverine;

namespace Billing.Application.Security;

public static class BillingPolicies
{
    /// <summary>Issuing, amending or revoking a grant.</summary>
    public const string GrantAdmin = "BillingGrantAdmin";

    /// <summary>Reading grants and their history.</summary>
    public const string GrantRead = "BillingGrantRead";
}

public class BillingStaffRequirement(bool adminOnly) : IAuthorizationRequirement
{
    public bool AdminOnly { get; } = adminOnly;
}

/// <summary>Resolves the caller's staff tier from Identity on every request.</summary>
public class BillingStaffRequirementCheck(IMessageBus bus, ILogger<BillingStaffRequirementCheck> logger)
    : AuthorizationHandler<BillingStaffRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, BillingStaffRequirement requirement)
    {
        // `sub` as a fallback because which of the two claims survives depends on
        // JwtBearerOptions.MapInboundClaims, and a claim-mapping change turning every staff request
        // into a 403 is a failure with no visible cause at all.
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? context.User.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(userId))
        {
            logger.LogWarning(
                "A billing staff request carried no subject claim, so no account could be checked. "
                + "Claims present: {Claims}. Denying.",
                string.Join(", ", context.User.Claims.Select(claim => claim.Type)));
            return;
        }

        try
        {
            var response = await bus.InvokeAsync<IsUserAdministrativeResponse>(
                new IsUserAdministrativeRequest { UserId = userId });

            var allowed = requirement.AdminOnly
                ? response.IsAdministrative
                : response.IsStaff || response.IsAdministrative;

            if (allowed)
            {
                context.Succeed(requirement);
                return;
            }

            logger.LogWarning(
                "Account {UserId} (role {Role}) was denied a billing surface requiring {Tier}.",
                userId,
                string.IsNullOrEmpty(response.Role) ? "unknown" : response.Role,
                requirement.AdminOnly ? "Admin" : "Moderator or Admin");
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "The staff check for {UserId} could not be completed - Identity did not answer over the "
                + "bus. Denying; this is an outage, not a permission decision.", userId);
        }
    }
}

public static class BillingPolicyServiceCollectionExtensions
{
    public static IServiceCollection AddBillingPolicies(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization(options =>
        {
            options.AddPolicy(BillingPolicies.GrantAdmin, policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new BillingStaffRequirement(adminOnly: true)));

            options.AddPolicy(BillingPolicies.GrantRead, policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new BillingStaffRequirement(adminOnly: false)));
        });

        services.AddScoped<IAuthorizationHandler, BillingStaffRequirementCheck>();

        return services;
    }
}
