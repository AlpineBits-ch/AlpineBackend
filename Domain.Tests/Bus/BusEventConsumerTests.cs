using System.Reflection;
using System.Text;
using Domain.Tests.Facets;

namespace Domain.Tests.Bus;

/// <summary>
/// Every message this repository publishes on the bus must have something that handles it.
/// </summary>
[TestFixture]
public class BusEventConsumerTests
{
    /// <summary>The orphans that already existed when this test was written.</summary>
    private static readonly HashSet<string> Exempt =
    [
        // Terminal event of the account-deletion saga, published for observability with nothing
        // downstream. Harmless today, but it means nobody is notified when a purge completes.
        "Identity.Contracts.Bus.Events.AccountDeletionCompletedEvent",

        // Isle player/voice events.
        "Isle.Contracts.Events.PlayerCreatedEvent",
        "Isle.Domain.Events.Player.PlayerUserIdLinked",
        "Isle.Domain.Events.Player.PlayerUserIdUnlinked",
        "Isle.Domain.Events.Voice.PlayerJoinedCell",
        "Isle.Domain.Events.Voice.PlayerLeftCell",
        "Isle.Domain.Events.Voice.RemovePlayer",
        "Isle.Domain.Events.Voice.UpdatePlayerPosition",

        // A payload shape that happens to live in a Bus.Commands namespace, not a message.
        "Messaging.Contracts.Bus.Commands.MinimalAttachmentContract",
    ];

    [Test]
    public void Every_published_bus_message_has_a_handler()
    {
        var types = RepositoryTypes.All();

        var published = PublishedMessageTypes(types);
        var handled = HandledMessageTypes(types);

        // Guard against passing vacuously because reflection broke.
        Assert.That(
            published,
            Is.Not.Empty,
            "No published bus messages were discovered at all. Discovery is broken - fix it rather "
                + "than deleting this test, otherwise nothing is being checked."
        );

        var orphans = published
            .Where(t => !handled.Contains(t))
            .Where(t => !Exempt.Contains(t.FullName ?? t.Name))
            .OrderBy(t => t.FullName)
            .ToList();

        if (orphans.Count == 0)
        {
            return;
        }

        var message = new StringBuilder()
            .AppendLine("These messages are published on the bus and nothing consumes them.")
            .AppendLine()
            .AppendLine("A publish with no consumer succeeds silently, so the behaviour it was meant")
            .AppendLine("to trigger simply never happens - and no test, log or error says so.")
            .AppendLine("Add a handler, stop publishing it, or add it to Exempt with a reason.")
            .AppendLine();

        foreach (var orphan in orphans)
        {
            message.AppendLine($"  {orphan.FullName}");
        }

        Assert.Fail(message.ToString());
    }

    /// <summary>Message types that reach the bus.</summary>
    private static IReadOnlyCollection<Type> PublishedMessageTypes(IReadOnlyList<Type> types)
    {
        var published = new HashSet<Type>();

        foreach (var type in types)
        {
            foreach (var method in DeclaredMethods(type))
            {
                foreach (var candidate in CascadedTypes(method.ReturnType))
                {
                    if (IsBusMessage(candidate)) published.Add(candidate);
                }
            }
        }

        return published;
    }

    /// <summary>
    /// The cascaded message types in a return type: the type itself, whatever a
    /// <c>Task&lt;T&gt;</c>/<c>ValueTask&lt;T&gt;</c> wraps, and every element of a returned tuple.
    /// </summary>
    private static IEnumerable<Type> CascadedTypes(Type returnType)
    {
        var unwrapped = Unwrap(returnType);

        if (!IsTuple(unwrapped))
        {
            yield return unwrapped;
            yield break;
        }

        foreach (var argument in unwrapped.GetGenericArguments())
        {
            yield return Nullable.GetUnderlyingType(argument) ?? argument;
        }
    }

    private static Type Unwrap(Type type)
    {
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
            {
                return Unwrap(type.GetGenericArguments()[0]);
            }
        }

        return Nullable.GetUnderlyingType(type) ?? type;
    }

    private static bool IsTuple(Type type) =>
        type.IsGenericType && type.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true;

    /// <summary>Message types something in this repository handles.</summary>
    private static IReadOnlyCollection<Type> HandledMessageTypes(IReadOnlyList<Type> types)
    {
        var handled = new HashSet<Type>();

        foreach (var type in types)
        {
            foreach (var method in DeclaredMethods(type))
            {
                // Matched on the method name alone, not on the containing type's name.
                var isHandlerMethod =
                    method.Name is "Handle" or "HandleAsync" or "Handles" or "Consume" or "ConsumeAsync"
                        or "Start" or "StartAsync" or "Orchestrate" or "OrchestrateAsync";

                if (!isHandlerMethod) continue;

                // The message is the first parameter by Wolverine's convention; the rest are
                // injected services.
                var first = method.GetParameters().FirstOrDefault();
                if (first is not null && IsBusMessage(first.ParameterType)) handled.Add(first.ParameterType);
            }
        }

        return handled;
    }

    private static IEnumerable<MethodInfo> DeclaredMethods(Type type)
    {
        try
        {
            return type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Whether a type is one of our bus messages.</summary>
    private static bool IsBusMessage(Type type)
    {
        if (type is not { IsClass: true, IsAbstract: false }) return false;
        if (type == typeof(object) || type == typeof(string)) return false;

        // A type nobody outside its declaring class can name cannot be routed: Wolverine has to
        // construct and deserialize it in another process.
        if (type.IsNestedPrivate || type.IsNestedAssembly || type.IsNestedFamANDAssem) return false;

        // A *Response is the reply to an InvokeAsync, not something published.
        if (type.Name.EndsWith("Response", StringComparison.Ordinal)) return false;

        var ns = type.Namespace;
        if (ns is null) return false;

        // Only the assemblies this repository builds.
        if (!IsRepositoryNamespace(ns)) return false;

        // Dtos are payload shapes, not messages, even when the folder is called Events.
        if (ns.Contains(".Dtos", StringComparison.Ordinal)) return false;

        return ns.Contains(".Bus.Events", StringComparison.Ordinal)
               || ns.Contains(".Bus.Commands", StringComparison.Ordinal)
               || ns.Contains(".Bus.Integration.Events", StringComparison.Ordinal)
               || ns.Contains(".Domain.Events", StringComparison.Ordinal)
               || ns.Contains(".Contracts.Events", StringComparison.Ordinal);
    }

    /// <summary>The service prefixes this repository builds.</summary>
    private static readonly string[] ServicePrefixes =
    [
        "Identity.", "Messaging.", "Guild.", "Social.", "Isle.", "Federation.", "Import.", "Bots.", "Echo.",
    ];

    private static bool IsRepositoryNamespace(string ns) =>
        ServicePrefixes.Any(p => ns.StartsWith(p, StringComparison.Ordinal));
}
