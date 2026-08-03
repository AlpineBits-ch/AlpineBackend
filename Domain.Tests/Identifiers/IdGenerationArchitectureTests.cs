using System.Reflection;
using System.Xml.Linq;
using Domain.Tests.Facets;
using Ids;
using Persistence;

// See the note in IdentifierTests on why this is not "Domain.Tests.Ids".
namespace Domain.Tests.Identifiers;

/// <summary>
/// Guards the invariant that there is exactly one way to mint an id.
///
/// The build already stops the obvious bypass: Ids.csproj declares the ULID package with
/// <c>PrivateAssets="compile"</c>, so the type is not nameable outside that project. These tests
/// cover what the compiler cannot - somebody adding their own package reference to get around it,
/// and prefixes silently colliding.
///
/// Discovery is by reflection and by scanning the .csproj files on disk, so there is no list to keep
/// up to date.
/// </summary>
[TestFixture]
public class IdGenerationArchitectureTests
{
    /// <summary>The project allowed to see the id implementation. Everything else goes through
    /// <see cref="Identifier"/>.</summary>
    private const string GeneratorProject = "Ids";

    /// <summary>The NuGet package backing <see cref="Identifier"/>. Named here so that swapping the
    /// implementation is a deliberate edit to this test rather than a silent change.</summary>
    private const string IdPackage = "Ulid";

    /// <summary>The package that used to mint ids. Removed outright; it must not come back.</summary>
    private const string RemovedIdPackage = "KsuidDotNet";

    // ══════════════════════════════════════════════════════════════════════════
    // Nothing may reach around the generator
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Only_the_Ids_project_may_reference_the_id_package()
    {
        var offenders = ProjectsReferencing(IdPackage)
            .Where(p => !string.Equals(p.Project, GeneratorProject, StringComparison.Ordinal))
            .ToList();

        Assert.That(offenders, Is.Empty,
            $"Only {GeneratorProject} may reference the {IdPackage} package - every other project "
            + "must mint ids through Ids.Identifier, so that prefixing, casing and encoding are "
            + $"decided in one place. Offending project(s): {Describe(offenders)}");
    }

    [Test]
    public void The_id_package_is_referenced_with_PrivateAssets_so_it_cannot_leak_to_consumers()
    {
        var generator = ProjectsReferencing(IdPackage)
            .SingleOrDefault(p => string.Equals(p.Project, GeneratorProject, StringComparison.Ordinal));

        Assert.That(generator, Is.Not.Null,
            $"{GeneratorProject} no longer references {IdPackage} at all - either the implementation "
            + "moved and this test needs updating, or id generation is quietly broken.");

        // This attribute is the entire enforcement mechanism. Without it the PackageReference flows
        // transitively and every service can name the type again, which is exactly the situation
        // that produced five bypassing call sites in Bots.Application.
        Assert.That(generator!.PrivateAssets, Is.EqualTo("compile").IgnoreCase,
            $"{GeneratorProject}'s {IdPackage} reference must keep PrivateAssets=\"compile\". It is "
            + "what stops the type being nameable outside this project while still shipping the "
            + "assembly at runtime.");
    }

    [Test]
    public void The_removed_id_package_has_not_come_back()
    {
        var offenders = ProjectsReferencing(RemovedIdPackage).ToList();

        Assert.That(offenders, Is.Empty,
            $"{RemovedIdPackage} minted upper-cased base62 ids, which are not sortable - upper-casing "
            + "base62 folds a-z onto A-Z and destroys the ordering. It was removed deliberately. "
            + $"Offending project(s): {Describe(offenders)}");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Prefixes are the type tag, so they have to be distinct and well formed
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Every_entity_prefix_is_unique()
    {
        var collisions = PrefixedEntities()
            .GroupBy(x => x.Prefix, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();

        if (collisions.Count == 0) return;

        var message = string.Join(
            Environment.NewLine,
            collisions.Select(g => $"  \"{g.Key}\" is used by: {string.Join(", ", g.Select(x => x.Type.FullName))}"));

        Assert.Fail(
            "Two entity types mint ids under the same prefix. Ids stay unique (the ULID body "
            + "guarantees that), but the prefix exists so an id says what it identifies - and a "
            + "shared tag makes that impossible, in logs and at every call site that passes one id "
            + "where another belongs."
            + Environment.NewLine + Environment.NewLine + message);
    }

    [Test]
    public void Every_entity_prefix_is_well_formed()
    {
        var malformed = PrefixedEntities()
            .Where(x => !IsWellFormed(x.Prefix))
            .Select(x => $"  {x.Type.FullName} -> \"{x.Prefix}\"")
            .ToList();

        Assert.That(malformed, Is.Empty,
            "An id prefix must be non-empty, lowercase, free of whitespace, and must not carry its "
            + "own trailing separator (Identifier.New adds it). Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, malformed));
    }

    [Test]
    public void Every_entity_actually_mints_ids_carrying_its_own_prefix()
    {
        // Reflection over the discovered types rather than a hand-written list: this is what catches
        // a type that implements IPrefixedEntity but hand-rolls GenerateId to do something else.
        var entities = PrefixedEntities().ToList();

        Assert.That(entities, Is.Not.Empty,
            "No IPrefixedEntity implementations were discovered at all. Reflection-based discovery "
            + "is broken - fix it rather than deleting this test, otherwise nothing is checked.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Discovery
    // ══════════════════════════════════════════════════════════════════════════

    private sealed record PackageUse(string Project, string? PrivateAssets);

    private sealed record PrefixedEntity(Type Type, string Prefix);

    private static bool IsWellFormed(string prefix) =>
        !string.IsNullOrWhiteSpace(prefix)
        && prefix == prefix.ToLowerInvariant()
        && !prefix.Any(char.IsWhiteSpace)
        && !prefix.EndsWith(Identifier.Separator);

    private static string Describe(IEnumerable<PackageUse> uses) =>
        string.Join(", ", uses.Select(u => u.Project).DefaultIfEmpty("(none)"));

    /// <summary>Every project in the repository that declares a PackageReference to
    /// <paramref name="packageId"/>, read from the .csproj files themselves.
    ///
    /// Deliberately source-based rather than reflection-based: a package that has been referenced
    /// but not yet called leaves no trace in the compiled output, and that is precisely the state to
    /// catch - somebody has just re-opened the door.</summary>
    private static IEnumerable<PackageUse> ProjectsReferencing(string packageId)
    {
        foreach (var path in ProjectFiles())
        {
            XDocument document;
            try
            {
                document = XDocument.Load(path);
            }
            catch (Exception)
            {
                // An unparseable csproj is not this test's problem; the build will have said so.
                continue;
            }

            foreach (var reference in document.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (!string.Equals(include, packageId, StringComparison.OrdinalIgnoreCase)) continue;

                // PrivateAssets can be written as an attribute or a child element; both are valid
                // MSBuild and a test that only understood one would be trivially bypassed.
                var privateAssets =
                    reference.Attribute("PrivateAssets")?.Value
                    ?? reference.Elements().FirstOrDefault(e => e.Name.LocalName == "PrivateAssets")?.Value;

                yield return new PackageUse(Path.GetFileNameWithoutExtension(path), privateAssets);
            }
        }
    }

    private static IEnumerable<string> ProjectFiles()
    {
        var root = RepositoryRoot();

        return Directory
            .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            // .claude/worktrees holds complete checkouts of this repository. Scanning them would
            // report every finding once per worktree and, worse, let a stale copy fail the build.
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}.claude{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    /// <summary>Walks up from the test binaries until it finds the solution file.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Echo.sln")))
        {
            directory = directory.Parent;
        }

        Assert.That(directory, Is.Not.Null,
            "Could not locate Echo.sln by walking up from the test output directory. This test reads "
            + "the repository's .csproj files, so it cannot run without it.");

        return directory!.FullName;
    }

    /// <summary>Every concrete <see cref="IPrefixedEntity"/> in the solution, paired with the prefix
    /// it declares. Deduplicated by full name, since the test output folder can hold more than one
    /// copy of an assembly.</summary>
    private static IEnumerable<PrefixedEntity> PrefixedEntities()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in RepositoryTypes.All())
        {
            if (type.IsInterface || type.IsAbstract || type.ContainsGenericParameters) continue;
            if (!typeof(IPrefixedEntity).IsAssignableFrom(type)) continue;
            if (type.FullName is null || !seen.Add(type.FullName)) continue;

            var property = type.GetProperty("Prefix", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (property?.GetValue(null) is not string prefix) continue;

            yield return new PrefixedEntity(type, prefix);
        }
    }
}
