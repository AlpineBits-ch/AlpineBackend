using System.Reflection;
using System.Text;

namespace Echo.TestConventions;

/// <summary>
/// Wolverine's conventional handler discovery matches an exact type-name suffix of "Handler" or
/// "Consumer" - not Contains, and not method-based. A class with a public Handle/Handles/Consume/
/// Consumes method whose name misses that suffix is never scanned: no log, no warning, no
/// exception, the feature is just dead. This is how eleven handler classes across four services
/// went silently unregistered in production.
/// </summary>
[TestFixture]
public class WolverineHandlerNamingConventionTests
{
    private static readonly string[] HandlerMethodNames = ["Handle", "Handles", "Consume", "Consumes"];
    private static readonly string[] DiscoverableSuffixes = ["Handler", "Consumer"];

    [Test]
    public void Every_class_with_a_handler_method_is_named_so_Wolverine_will_find_it()
    {
        var assembly = ServiceAssembly();
        var (offenders, handlerMethodCount) = Scan(assembly);

        // A scan that silently found nothing would pass forever without checking anything - this
        // proves the assembly actually resolved and genuinely has handler-shaped methods in it.
        Assert.That(handlerMethodCount, Is.GreaterThan(0),
            $"No candidate handler methods were found in {assembly.GetName().Name}. Either this "
            + "service has genuinely dropped Wolverine, or assembly resolution is broken - fix "
            + "ServiceAssembly() rather than deleting this test.");

        if (offenders.Count > 0)
            Assert.Fail(Describe(assembly, offenders));
    }

    /// <summary>Guards the guard: a plural offender must be caught, a correctly named one must not.</summary>
    [Test]
    public void The_detector_flags_a_plural_class_and_ignores_a_correctly_named_one()
    {
        var offenderMethod = typeof(Offenders.OrderCreatedHandlers).GetMethod(nameof(Offenders.OrderCreatedHandlers.Handle))!;
        var cleanMethod = typeof(Offenders.OrderCreatedHandler).GetMethod(nameof(Offenders.OrderCreatedHandler.Handle))!;

        Assert.Multiple(() =>
        {
            Assert.That(IsHandlerMethod(offenderMethod), Is.True, "a public Handle(message) method must be recognised");
            Assert.That(IsDiscoverable(typeof(Offenders.OrderCreatedHandlers)), Is.False,
                "OrderCreatedHandlers is plural - Wolverine would never scan it");
            Assert.That(IsDiscoverable(typeof(Offenders.OrderCreatedHandler)), Is.True,
                "OrderCreatedHandler ends in the right suffix and must not be flagged");
            Assert.That(IsHandlerMethod(cleanMethod), Is.True);

            // A zero-parameter method named Handle is not a Wolverine message handler.
            var noArgs = typeof(Offenders.NotAHandler).GetMethod(nameof(Offenders.NotAHandler.Handle))!;
            Assert.That(IsHandlerMethod(noArgs), Is.False, "Handle() with no parameters carries no message");
        });
    }

    // ── the check ────────────────────────────────────────────────────────────

    private static (List<(Type Type, MethodInfo Method)> Offenders, int HandlerMethodCount) Scan(Assembly assembly)
    {
        var offenders = new List<(Type, MethodInfo)>();
        var handlerMethodCount = 0;

        // A type that fails to load must not take the whole scan down with it.
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
            if (type is null || !IsConcretePublicClass(type)) continue;

            var handlerMethods = HandlerMethodsOf(type);
            if (handlerMethods.Length == 0) continue;

            handlerMethodCount += handlerMethods.Length;

            if (!IsDiscoverable(type))
                offenders.AddRange(handlerMethods.Select(m => (type, m)));
        }

        return (offenders, handlerMethodCount);
    }

    private static bool IsConcretePublicClass(Type type) =>
        type.IsClass && !type.IsAbstract && (type.IsPublic || type.IsNestedPublic);

    private static MethodInfo[] HandlerMethodsOf(Type type)
    {
        MethodInfo[] methods;
        try
        {
            methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        }
        catch (TypeLoadException)
        {
            return [];
        }

        return methods.Where(IsHandlerMethod).ToArray();
    }

    private static bool IsHandlerMethod(MethodInfo method) =>
        HandlerMethodNames.Contains(method.Name, StringComparer.Ordinal) && method.GetParameters().Length >= 1;

    private static bool IsDiscoverable(Type type) =>
        DiscoverableSuffixes.Any(suffix => type.Name.EndsWith(suffix, StringComparison.Ordinal));

    // ── assembly resolution ─────────────────────────────────────────────────

    /// <summary>
    /// This file is linked into several test projects (see their .csproj Compile Include), so it
    /// cannot hardcode which assembly to scan. Each test project it is linked into references
    /// exactly one *.Application project - that convention picks the target at runtime instead.
    /// </summary>
    private static Assembly ServiceAssembly()
    {
        var candidates = Assembly.GetExecutingAssembly().GetReferencedAssemblies()
            .Where(name => name.Name is not null && name.Name.EndsWith(".Application", StringComparison.Ordinal))
            .ToArray();

        Assert.That(candidates, Has.Length.EqualTo(1),
            "Expected exactly one *.Application reference to resolve the service assembly, found "
            + $"{candidates.Length}: {string.Join(", ", candidates.Select(c => c.Name))}. This test "
            + "project's assembly-resolution convention no longer holds - fix ServiceAssembly().");

        return Assembly.Load(candidates[0]);
    }

    // ── the message, which is the point ─────────────────────────────────────

    private static string Describe(Assembly assembly, IReadOnlyCollection<(Type Type, MethodInfo Method)> offenders)
    {
        var message = new StringBuilder();

        message.AppendLine($"{offenders.Count} handler method(s) in {assembly.GetName().Name} live on a class Wolverine will never scan.");
        message.AppendLine();
        message.AppendLine(
            "Wolverine's conventional discovery matches an exact type-name suffix of \"Handler\" or "
            + "\"Consumer\", not Contains, and it is not method-based. A misnamed class is silently "
            + "skipped: no log, no warning, no exception. The message type named below has no live "
            + "consumer.");
        message.AppendLine();

        foreach (var (type, method) in offenders.OrderBy(o => o.Type.FullName, StringComparer.Ordinal))
        {
            var messageType = method.GetParameters()[0].ParameterType.Name;
            message.AppendLine($"  {type.FullName}.{method.Name}({messageType} ...) - {messageType} has no live consumer");
        }

        message.AppendLine();
        message.AppendLine("Rename the class to end in Handler or Consumer so Wolverine's convention picks it up.");

        return message.ToString();
    }

    /// <summary>Known-shape types for the detector to be tested against.</summary>
    private static class Offenders
    {
        internal sealed record Message;

        internal class OrderCreatedHandlers
        {
            public void Handle(Message message) { }
        }

        internal class OrderCreatedHandler
        {
            public void Handle(Message message) { }
        }

        internal class NotAHandler
        {
            public void Handle() { }
        }
    }
}
