using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Domain.Tests.Facets;

/// <summary>
/// The set of CLR types EF Core maps as entities, taken from the real EF model of every
/// <see cref="DbContext"/> in the repository.
///
/// Reading the built model rather than the <c>DbSet&lt;T&gt;</c> declarations matters: plenty of
/// entities are only reachable through <c>modelBuilder.Entity&lt;T&gt;()</c> or through a
/// navigation, and those are exactly the ones that close a reference loop.
/// </summary>
internal static class DatabaseEntities
{
    /// <summary>
    /// Never opened. Model building is entirely offline; the contexts only need a provider so that
    /// provider-specific mapping calls (jsonb columns, enum mappings, NetTopologySuite) resolve.
    /// </summary>
    private const string OfflineConnectionString =
        "Host=architecture-test.invalid;Database=architecture-test;Username=none;Password=none";

    public static IReadOnlySet<Type> Discover(IReadOnlyList<Type> types)
    {
        var contexts = types
            .Where(t =>
                typeof(DbContext).IsAssignableFrom(t) && t is { IsAbstract: false, IsGenericTypeDefinition: false }
            )
            .OrderBy(t => t.FullName)
            .ToList();

        var entities = new HashSet<Type>();
        var failures = new List<string>();

        foreach (var contextType in contexts)
        {
            DbContext? context;
            try
            {
                context = Create(contextType, types);
            }
            catch (Exception ex)
            {
                failures.Add($"  {contextType.FullName}: {Unwrap(ex).Message}");
                continue;
            }

            using (context)
            {
                try
                {
                    foreach (var entityType in context.Model.GetEntityTypes())
                    {
                        // Owned types are value objects stored inline with their owner. They are
                        // legitimate to expose from a facet and cannot form a cycle by themselves.
                        if (entityType.IsOwned())
                        {
                            continue;
                        }

                        entities.Add(entityType.ClrType);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"  {contextType.FullName}: {Unwrap(ex).Message}");
                }
            }
        }

        if (failures.Count > 0)
        {
            Assert.Fail(
                "Could not build the EF model for the following DbContext(s), so their entities "
                    + "would not be checked:\n"
                    + string.Join("\n", failures)
                    + "\n\nGive the context a public `(DbContextOptions<TContext> options)` constructor "
                    + "or an IDesignTimeDbContextFactory<TContext>, so the model can be built offline."
            );
        }

        return entities;
    }

    private static DbContext Create(Type contextType, IReadOnlyList<Type> types)
    {
        var builderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(builderType)!;
        builder.UseNpgsql(OfflineConnectionString);

        try
        {
            return (DbContext)Activator.CreateInstance(contextType, builder.Options)!;
        }
        catch (Exception) when (DesignTimeFactory(contextType, types) is not null)
        {
            var factory = DesignTimeFactory(contextType, types)!;
            var instance = Activator.CreateInstance(factory)!;
            return (DbContext)
                factory.GetMethod(nameof(IDesignTimeDbContextFactory<DbContext>.CreateDbContext))!
                    .Invoke(instance, [Array.Empty<string>()])!;
        }
    }

    private static Type? DesignTimeFactory(Type contextType, IReadOnlyList<Type> types)
    {
        var factoryInterface = typeof(IDesignTimeDbContextFactory<>).MakeGenericType(contextType);
        return types.FirstOrDefault(t =>
            t is { IsAbstract: false, IsGenericTypeDefinition: false }
            && factoryInterface.IsAssignableFrom(t)
            && t.GetConstructor(Type.EmptyTypes) is not null
        );
    }

    private static Exception Unwrap(Exception ex) =>
        ex is System.Reflection.TargetInvocationException { InnerException: { } inner }
            ? Unwrap(inner)
            : ex;
}
