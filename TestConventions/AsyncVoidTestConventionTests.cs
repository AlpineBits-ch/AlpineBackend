using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Echo.TestConventions;

/// <summary>
/// Fails the build when anything in a test assembly compiles to an <c>async void</c> method.
/// </summary>
[TestFixture]
public class AsyncVoidTestConventionTests
{
    /// <summary>The version that made <c>Assert.Multiple(async ...)</c> safe, by adding the
    /// <c>Func&lt;Task&gt;</c> overload. Named in the failure message because the version is
    /// almost always the actual cause.</summary>
    private const string NUnitVersionThatFixedAssertMultiple = "4.6";

    [Test]
    public void No_code_in_this_assembly_compiles_to_an_async_void_method()
    {
        var (offenders, methodsExamined) = Scan(Assembly.GetExecutingAssembly());

        // Without this, a reflection failure that returned no types at all would look identical to
        // a clean assembly, and the guard would quietly stop guarding.
        Assert.That(methodsExamined, Is.GreaterThan(100),
            $"Only {methodsExamined} methods were examined, which is too few for a test assembly. "
            + "Reflection-based discovery is broken - fix it rather than deleting this test, "
            + "otherwise nothing is being checked.");

        if (offenders.Count > 0)
        {
            Assert.Fail(Describe(offenders));
        }
    }

    /// <summary>Guards the guard.</summary>
    [Test]
    public void The_detector_recognises_an_async_void_method_and_ignores_the_honest_ones()
    {
        Assert.Multiple(() =>
        {
            Assert.That(IsAsyncVoid(CanaryMethod(nameof(DeliberatelyAsyncVoid.FireAndForget))), Is.True,
                "the detector no longer recognises an async void method, so the scan above is inert");

            Assert.That(IsAsyncVoid(CanaryMethod(nameof(DeliberatelyAsyncVoid.ProperlyAsync))), Is.False,
                "an async Task method is the correct shape and must never be flagged");

            Assert.That(IsAsyncVoid(CanaryMethod(nameof(DeliberatelyAsyncVoid.PlainVoid))), Is.False,
                "a plain void method that starts no state machine is innocent and must not be flagged");

            Assert.That(IsAsyncVoid(CanaryMethod(nameof(DeliberatelyAsyncVoid.Iterator))), Is.False,
                "an iterator is a state machine too, but not an async one - it must not be flagged");
        });
    }

    /// <summary>The canary is itself async void, so the scan has to skip it by exact type identity -
    /// deliberately not by a name pattern, which would also wave through a real offender that
    /// happened to match.</summary>
    [Test]
    public void The_canary_is_excluded_from_the_scan_by_identity_only()
    {
        Assert.Multiple(() =>
        {
            Assert.That(IsExcluded(typeof(DeliberatelyAsyncVoid)), Is.True);
            Assert.That(IsExcluded(typeof(AsyncVoidTestConventionTests)), Is.False,
                "only the canary is exempt - not this fixture, and not anything merely nested in it");
        });
    }

    // ── the check ─────────────────────────────────────────────────────────────

    /// <summary>
    /// An async void method carries <see cref="AsyncStateMachineAttribute"/> and returns void.
    /// </summary>
    private static bool IsAsyncVoid(MethodInfo? method) =>
        method is not null
        && method.ReturnType == typeof(void)
        && method.GetCustomAttribute<AsyncStateMachineAttribute>() is not null;

    private static bool IsExcluded(Type type) => type == typeof(DeliberatelyAsyncVoid);

    private static (List<MethodInfo> Offenders, int MethodsExamined) Scan(Assembly assembly)
    {
        var offenders = new List<MethodInfo>();
        var examined = 0;

        // A type that fails to load must not take the whole scan down with it - the remaining types
        // are still worth checking, and the load failure will surface in its own tests.
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }

        foreach (var type in types)
        {
            if (type is null || IsExcluded(type)) continue;

            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.Static
                                          | BindingFlags.DeclaredOnly);
            }
            catch (TypeLoadException)
            {
                continue;
            }

            examined += methods.Length;
            offenders.AddRange(methods.Where(IsAsyncVoid));
        }

        return (offenders, examined);
    }

    // ── the message, which is the point ───────────────────────────────────────

    private static string Describe(IReadOnlyCollection<MethodInfo> offenders)
    {
        var assembly = Assembly.GetExecutingAssembly().GetName().Name;
        var message = new StringBuilder();

        message.AppendLine($"{offenders.Count} async void method(s) in {assembly}.");
        message.AppendLine();
        message.AppendLine(
            "An async void method returns to its caller at the first await. For a test method, or "
            + "for the lambda passed to Assert.Multiple, that means NUnit has already moved on by "
            + "the time the rest of the body runs: every assertion after the first await lands "
            + "outside the test, cannot fail it, and therefore passes no matter what it asserts.");
        message.AppendLine();

        foreach (var offender in offenders.OrderBy(Origin, StringComparer.Ordinal))
        {
            message.AppendLine($"  {Origin(offender)}");
            if (LambdaNote(offender) is { } note) message.AppendLine($"    {note}");
            if (SourceFile(offender) is { } file) message.AppendLine($"    {file}");
        }

        message.AppendLine();
        message.AppendLine(
            $"The usual cause is the NUnit version. Assert.Multiple only gained its Func<Task> "
            + $"overload in NUnit {NUnitVersionThatFixedAssertMultiple}; below that its overloads "
            + "are void-returning (TestDelegate, Action), so `Assert.Multiple(async () => ...)` "
            + "binds to a void delegate and becomes async void silently - no compiler error, no "
            + "warning, and the suite stays green while it stops checking anything.");
        message.AppendLine();
        message.AppendLine($"    NUnit in this assembly: {NUnitVersion()}");
        message.AppendLine();
        message.AppendLine(
            $"Restore NUnit >= {NUnitVersionThatFixedAssertMultiple}. If you cannot, rewrite each "
            + "site to await outside the block and assert on the awaited values inside a "
            + "synchronous Assert.Multiple - that form does not depend on the overload set at all.");
        message.AppendLine();
        message.AppendLine(
            "If a hit here is a deliberate fire-and-forget rather than an assertion block, it still "
            + "does not belong in test code - give it a Task-returning signature and await it, so a "
            + "failure inside it can reach the test.");

        return message.ToString();
    }

    /// <summary>The human location: the class somebody actually wrote, and the method they wrote it
    /// in. Both are mangled for a lambda (<c>&lt;&gt;c</c> / <c>&lt;Method&gt;b__0_0</c>), and the
    /// mangled form is unsearchable, so it is unpicked back to the source names here.</summary>
    private static string Origin(MethodInfo method)
    {
        var type = method.DeclaringType;
        while (type?.DeclaringType is not null && IsCompilerGenerated(type)) type = type.DeclaringType;

        return $"{type?.FullName ?? "<unknown type>"}.{SourceMethodName(method.Name)}";
    }

    private static string SourceMethodName(string name)
    {
        // `<EnclosingMethod>b__12_0` (lambda) and `<EnclosingMethod>g__Local|0_0` (local function)
        // both carry the name of the method the developer wrote between the angle brackets.
        if (!name.StartsWith('<')) return name;

        var close = name.IndexOf('>');
        return close > 1 ? name[1..close] : name;
    }

    private static string? LambdaNote(MethodInfo method)
    {
        if (!method.Name.StartsWith('<')) return null;

        return method.Name.Contains(">b__", StringComparison.Ordinal)
            ? "in a lambda inside that method - most likely the Assert.Multiple block"
            : "in a local function inside that method";
    }

    /// <summary>Best-effort source path, so the reader can open the thing rather than search for it.
    /// Reflection cannot give a file, so the class name is resolved against the repository the same
    /// way the other architecture tests locate it. Absent (rather than wrong) if that fails.</summary>
    private static string? SourceFile(MethodInfo method)
    {
        var type = method.DeclaringType;
        while (type?.DeclaringType is not null && IsCompilerGenerated(type)) type = type.DeclaringType;
        if (type is null) return null;

        var name = type.Name.Contains('`') ? type.Name[..type.Name.IndexOf('`')] : type.Name;

        var root = RepositoryRoot();
        if (root is null) return null;

        try
        {
            var match = root.EnumerateFiles($"{name}.cs", SearchOption.AllDirectories)
                .FirstOrDefault(f => !IsBuildOutput(f.FullName));

            return match is null ? null : Path.GetRelativePath(root.FullName, match.FullName).Replace('\\', '/');
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    private static DirectoryInfo? RepositoryRoot()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Echo.sln"))) dir = dir.Parent;
        return dir;
    }

    private static bool IsCompilerGenerated(Type type) =>
        type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null;

    /// <summary>Printed so the reader can rule the version in or out at a glance.</summary>
    private static string NUnitVersion()
    {
        var informational = typeof(Assert).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (informational is not null)
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return typeof(Assert).Assembly.GetName().Version?.ToString() ?? "unknown";
    }

    private static MethodInfo? CanaryMethod(string name) =>
        typeof(DeliberatelyAsyncVoid).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);

    /// <summary>Known-shape methods for the detector to be tested against.</summary>
    private static class DeliberatelyAsyncVoid
    {
        internal static async void FireAndForget() => await Task.Yield();

        internal static async Task ProperlyAsync() => await Task.Yield();

        internal static void PlainVoid() { }

        internal static IEnumerable<int> Iterator() { yield return 1; }
    }
}
