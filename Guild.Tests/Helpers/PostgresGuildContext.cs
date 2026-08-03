using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Helpers;

/// <summary>
/// A <see cref="MicroserviceContext"/> configured with the real Npgsql provider, pointed at a host
/// that is never contacted.
/// </summary>
internal sealed class PostgresGuildContext() : MicroserviceContext(
    new DbContextOptionsBuilder<MicroserviceContext>().Options);
