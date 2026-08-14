using Guild.Application.Services;
using Guild.Domain.Events.Role;
using Guild.Persistence.Persistence;

namespace Guild.Application.Bus.Events.Role;

/// <summary>
/// Drops the references to a deleted role that no foreign key covers - onboarding grant lists and
/// chore rotation pools.
/// </summary>
public class RoleDeletedCleanupHandler
{
    public static async Task Handle(
        RoleDeleted @event,
        RoleReferenceCleanupService cleanup,
        // Present so AutoApplyTransactions sees a DbContext on this chain and commits what the
        // service tracked - it keys off the signature, and the service's own injected context is the
        // same scoped instance. Deliberately not used directly here, same as GuildPrivacyEndpoint.
        MicroserviceContext ctx)
    {
        await cleanup.CleanupAsync(@event.GuildId, @event.RoleId);
    }
}
