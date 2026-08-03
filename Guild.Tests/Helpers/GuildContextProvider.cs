using Guild.Persistence.Persistence;

namespace Guild.Tests.Helpers;

/// <summary>
/// How a test gets its <see cref="MicroserviceContext"/>.
///
/// <para>Exists so one behavioural suite can be run against both providers rather than written
/// twice. See <see cref="PostgresContextProvider"/> for why running it twice is the point.</para>
/// </summary>
public interface IGuildContextProvider
{
    /// <summary>Shown in the test name, so a failure says which provider produced it.</summary>
    string Name { get; }

    /// <summary>A context over an empty database. Called once per test.</summary>
    Task<MicroserviceContext> CreateAsync();
}

/// <summary>
/// The EF InMemory provider: fast, no infrastructure, and structurally unable to catch a query that
/// does not translate to SQL. Good for asserting behaviour on every run.
/// </summary>
public sealed class InMemoryContextProvider : IGuildContextProvider
{
    public string Name => "InMemory";

    public Task<MicroserviceContext> CreateAsync() =>
        Task.FromResult<MicroserviceContext>(new TestGuildContext(Guid.NewGuid().ToString()));

    public override string ToString() => Name;
}

/// <summary>
/// A real Postgres in a container.
///
/// <para>The same assertions as the InMemory run, against the provider that actually ships. This is
/// the layer that catches an untranslatable query - and anything where Postgres and
/// LINQ-to-Objects disagree about ordering, collation, or NULLs.</para>
/// </summary>
public sealed class PostgresContextProvider : IGuildContextProvider
{
    public string Name => "Postgres";

    public async Task<MicroserviceContext> CreateAsync()
    {
        await PostgresTestDatabase.EnsureStartedAsync();
        await PostgresTestDatabase.ResetAsync();
        return new PostgresGuildContext();
    }

    public override string ToString() => Name;
}

/// <summary>
/// The fixture source. A suite marked <c>[TestFixtureSource(typeof(GuildContextProviders))]</c>
/// runs once per provider.
/// </summary>
public sealed class GuildContextProviders : IEnumerable<IGuildContextProvider>
{
    public IEnumerator<IGuildContextProvider> GetEnumerator()
    {
        yield return new InMemoryContextProvider();
        yield return new PostgresContextProvider();
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
