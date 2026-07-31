using System.Reflection;

namespace Domain.Tests.Facets;

/// <summary>Every type in every assembly that this repository produces.</summary>
internal static class RepositoryTypes
{
    private static readonly Lazy<IReadOnlyList<Type>> Cached = new(Load);

    public static IReadOnlyList<Type> All() => Cached.Value;

    private static IReadOnlyList<Type> Load()
    {
        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
        {
            try
            {
                Assembly.Load(AssemblyName.GetAssemblyName(path));
            }
            catch (Exception)
            {
                // Native libraries, resource-only satellites and assemblies whose dependencies are
                // not in this folder. None of them can hold a facet or a DbContext of ours.
            }
        }

        return AppDomain
            .CurrentDomain.GetAssemblies()
            .Where(IsRepositoryAssembly)
            .SelectMany(TypesOf)
            .ToList();
    }

    private static bool IsRepositoryAssembly(Assembly assembly)
    {
        if (assembly.IsDynamic)
        {
            return false;
        }

        try
        {
            return assembly.GetName().GetPublicKeyToken() is null or { Length: 0 };
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Partially loadable assembly: keep the types that did resolve.
            return ex.Types.Where(t => t is not null)!;
        }
        catch (Exception)
        {
            return [];
        }
    }
}
