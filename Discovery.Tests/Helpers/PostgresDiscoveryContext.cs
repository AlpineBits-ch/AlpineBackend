using Discovery.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests.Helpers;

/// <summary>
/// A <see cref="MicroserviceContext"/> configured with the real Npgsql provider, pointed at a host
/// that is never contacted. Exists so ToQueryString() can prove a LINQ query translates without
/// Docker - mirrors Guild.Tests.Helpers.PostgresGuildContext, the same pattern for the same reason.
/// </summary>
internal sealed class PostgresDiscoveryContext() : MicroserviceContext(
    new DbContextOptionsBuilder<MicroserviceContext>().Options);
