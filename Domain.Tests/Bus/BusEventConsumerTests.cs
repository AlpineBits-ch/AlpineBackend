using System.Reflection;
using System.Text;
using Domain.Tests.Facets;

namespace Domain.Tests.Bus;

/// <summary>
/// Every message this repository publishes on the bus must have something that handles it.
///
/// <para><b>Why this test exists.</b> <c>DeviceRegistered</c> and <c>DeviceRemoved</c> were both
/// declared, both published from a live endpoint, and both consumed by absolutely nothing. Removing
/// a device therefore deleted a database row and left the handset decrypting every future message in
/// every group it was ever added to; registering one told no conversation it had appeared, which is
/// most of why a second device could never join an existing conversation. Neither failure produced
/// an error, a log line, or a test failure - the publish succeeded and the message went nowhere.</para>
///
/// <para>That is a whole class of bug, and it is the kind that only shows up as "my phone can't read
/// this". Reflection over the assemblies rather than a list to maintain: a new event is covered the
/// moment it is published.</para>
/// </summary>
[TestFixture]
public class BusEventConsumerTests
{
    /// <summary>
    /// The orphans that already existed when this test was written.
    ///
    /// <para>This is a <b>baseline, not an approval</b>. Each of these is published with nothing
    /// consuming it today; they are recorded so the test can start guarding against <i>new</i>
    /// ones instead of being deleted for failing on the first run. Each entry says what is actually
    /// wrong with it, and the list is meant to shrink.</para>
    ///
    /// <para>Do not add to it to make a change pass. Adding a handler is the fix; if a message
    /// genuinely has no consumer by design, stop publishing it.</para>
    /// </summary>
    private static readonly HashSet<string> Exempt =
    [
        // Terminal event of the account-deletion saga, published for observability with nothing
        // downstream. Harmless today, but it means nobody is notified when a purge completes.
        "Identity.Contracts.Bus.Events.AccountDeletionCompletedEvent",

        // Isle player/voice events. Pre-existing and untouched by the MLS work; the voice cluster
        // handler returns IEnumerable<object>, so whatever routes these does not do it by the
        // convention this test understands. Worth a look by whoever owns Isle.
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

    /// <summary>
    /// Message types that reach the bus.
    ///
    /// <para>Two routes, and both count. The explicit one is a <c>PublishAsync</c>/<c>SendAsync</c>
    /// call. The quiet one is Wolverine's cascading-message convention: a handler or HTTP endpoint
    /// that returns <c>(IResult, TEvent?)</c> publishes <c>TEvent</c> without any call site to grep
    /// for - which is exactly how <c>DeviceRegistered</c> and <c>DeviceRemoved</c> came to be
    /// published from an endpoint that mentions neither verb.</para>
    /// </summary>
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

    /// <summary>
    /// Message types something in this repository handles.
    ///
    /// <para>Wolverine discovers handlers by convention - a public <c>Handle</c>/<c>Consume</c>
    /// method on a type whose name ends in Handler/Consumer - so this mirrors that convention rather
    /// than looking for an interface there is none of.</para>
    /// </summary>
    private static IReadOnlyCollection<Type> HandledMessageTypes(IReadOnlyList<Type> types)
    {
        var handled = new HashSet<Type>();

        foreach (var type in types)
        {
            foreach (var method in DeclaredMethods(type))
            {
                // Matched on the method name alone, not on the containing type's name. Wolverine's
                // discovery is method-driven, and this codebase has handlers in types called
                // SocialOutboundHandlers, AccountDeletion and RelationshipRealtimeHandlers - a
                // suffix filter quietly reports all of those as unhandled.
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

    /// <summary>
    /// Whether a type is one of our bus messages.
    ///
    /// <para>Namespace-scoped to <c>*.Contracts.Bus.*</c> and to domain event namespaces, which is
    /// what "a message that crosses the bus" means in this codebase. Without that scoping the
    /// cascading-return scan would sweep in every DTO an endpoint returns alongside its event.</para>
    /// </summary>
    private static bool IsBusMessage(Type type)
    {
        if (type is not { IsClass: true, IsAbstract: false }) return false;
        if (type == typeof(object) || type == typeof(string)) return false;

        // A type nobody outside its declaring class can name cannot be routed: Wolverine has to
        // construct and deserialize it in another process. Worth stating because records make this
        // easy to trip - the compiler gives every one of them a public clone method returning its
        // own type, so a private nested record inside a handler reads as a cascaded message.
        if (type.IsNestedPrivate || type.IsNestedAssembly || type.IsNestedFamANDAssem) return false;

        // A *Response is the reply to an InvokeAsync, not something published. Wolverine returns it
        // to the caller rather than routing it, so demanding a handler for one is nonsense.
        if (type.Name.EndsWith("Response", StringComparison.Ordinal)) return false;

        var ns = type.Namespace;
        if (ns is null) return false;

        // Only the assemblies this repository builds. NuGet packages that happen to be unsigned -
        // JasperFx.Events is a whole namespace of "Events" types - would otherwise flood the result
        // with types nobody here publishes.
        if (!IsRepositoryNamespace(ns)) return false;

        // Dtos are payload shapes, not messages, even when the folder is called Events.
        if (ns.Contains(".Dtos", StringComparison.Ordinal)) return false;

        return ns.Contains(".Bus.Events", StringComparison.Ordinal)
               || ns.Contains(".Bus.Commands", StringComparison.Ordinal)
               || ns.Contains(".Bus.Integration.Events", StringComparison.Ordinal)
               || ns.Contains(".Domain.Events", StringComparison.Ordinal)
               || ns.Contains(".Contracts.Events", StringComparison.Ordinal);
    }

    /// <summary>The service prefixes this repository builds. A new service is covered by adding its
    /// folder, which the wildcard ProjectReference in the csproj already does.</summary>
    private static readonly string[] ServicePrefixes =
    [
        "Identity.", "Messaging.", "Guild.", "Social.", "Isle.", "Federation.", "Import.", "Bots.", "Echo.",
    ];

    private static bool IsRepositoryNamespace(string ns) =>
        ServicePrefixes.Any(p => ns.StartsWith(p, StringComparison.Ordinal));
}
