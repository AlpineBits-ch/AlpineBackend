using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Docs.Generator;

/// <summary>One <c>hub.Clients.X.SendAsync("name", payload)</c> call site.</summary>
internal sealed record OutboundSite(
    string EventName,
    string PayloadClrType,
    bool PayloadIsAnonymous,
    PayloadSchema Schema,
    string File,
    int Line)
{
    /// <summary>Set when the name and/or payload came from a caller of a fan-out helper rather than
    /// from the send itself, so the docs can point at the real origin.</summary>
    public string? ViaHelper { get; init; }
}

/// <summary>One <c>[HubMethodName]</c> method on the hub.</summary>
internal sealed record InboundMethod(
    string EventName,
    string ClrMethod,
    IReadOnlyList<InboundParameter> Parameters,
    string? Summary,
    string File,
    int Line);

internal sealed record InboundParameter(string Name, string ClrType, PayloadSchema Schema);

/// <summary>
/// A send whose event name (and possibly payload) is a parameter of the enclosing method - a
/// fan-out helper like <c>HouseholdChannelService.BroadcastAsync(guildId, eventName, payload)</c>.
/// </summary>
internal sealed record DeferredSend(
    string HelperKey,
    string HelperDisplay,
    int NameOrdinal,
    int? PayloadOrdinal,
    PayloadSchema? LocalSchema,
    string LocalClrType,
    bool LocalAnonymous,
    string File,
    int Line);

/// <summary>Harvests the realtime contract straight out of the source.</summary>
internal sealed class RealtimeExtractor(SchemaBuilder schemas)
{
    private const string ClientProxyExtensions = "Microsoft.AspNetCore.SignalR.ClientProxyExtensions";

    private readonly List<DeferredSend> _deferred = [];

    public List<OutboundSite> Outbound { get; } = [];
    public List<InboundMethod> Inbound { get; } = [];
    public List<string> Unresolved { get; } = [];

    // ── Pass 1 ───────────────────────────────────────────────────────────────

    public async Task ScanAsync(Compilation compilation)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync();

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                TryOutbound(model, invocation);

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                TryInbound(model, method);
        }
    }

    private void TryOutbound(SemanticModel model, InvocationExpressionSyntax invocation)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol symbol) return;
        if (symbol.Name != "SendAsync") return;

        // Extension methods bind to their static container, reduced or not.
        if ((symbol.ReducedFrom ?? symbol).ContainingType?.ToDisplayString() != ClientProxyExtensions) return;

        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count < 2) return;

        var location = invocation.GetLocation().GetLineSpan();
        var file = location.Path;
        var line = location.StartLinePosition.Line + 1;

        var nameConstant = model.GetConstantValue(arguments[0].Expression);
        var payloadType = model.GetTypeInfo(arguments[1].Expression).Type;

        // Only the name decides whether this can be documented here.
        if (nameConstant is { HasValue: true, Value: string eventName })
        {
            if (payloadType is null)
            {
                Unresolved.Add($"{file}:{line} - unresolved payload type for '{eventName}'");
                return;
            }

            Outbound.Add(new OutboundSite(
                eventName, payloadType.ToDisplayString(), payloadType.IsAnonymousType,
                schemas.Build(payloadType), file, line));
            return;
        }

        // The name comes from a parameter - defer to the helper's callers.
        var nameOrdinal = ParameterOrdinal(model, arguments[0].Expression);
        var payloadOrdinal = ParameterOrdinal(model, arguments[1].Expression);
        var enclosing = EnclosingMethod(model, invocation);

        if (enclosing is null || nameOrdinal is null)
        {
            Unresolved.Add($"{file}:{line} - event name is neither a literal nor a parameter");
            return;
        }

        _deferred.Add(new DeferredSend(
            Key(enclosing), enclosing.ToDisplayString(),
            nameOrdinal.Value, payloadOrdinal,
            payloadOrdinal is null && payloadType is not null ? schemas.Build(payloadType) : null,
            payloadType?.ToDisplayString() ?? "unknown",
            payloadType?.IsAnonymousType ?? false,
            file, line));
    }

    // ── Pass 2: resolve fan-out helpers from their call sites ────────────────

    /// <summary>Walks fan-out helpers back to their call sites, repeatedly.</summary>
    public async Task ResolveDeferredAsync(IReadOnlyList<Compilation> compilations)
    {
        const int maxHops = 5;
        if (_deferred.Count == 0) return;

        // Index every invocation once; the loop below re-reads it each round.
        var callSites = new List<(SemanticModel Model, InvocationExpressionSyntax Node, string Key)>();

        foreach (var compilation in compilations)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync();

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol called)
                        callSites.Add((model, invocation, Key(called)));
                }
            }
        }

        var pending = _deferred.ToList();
        var seen = new HashSet<string>(pending.Select(d => d.HelperKey), StringComparer.Ordinal);

        for (var hop = 0; hop < maxHops && pending.Count > 0; hop++)
        {
            var byKey = pending.ToLookup(d => d.HelperKey, StringComparer.Ordinal);
            var next = new List<DeferredSend>();
            var reached = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (model, node, key) in callSites)
            {
                if (!byKey.Contains(key)) continue;

                foreach (var deferred in byKey[key])
                {
                    if (ResolveOne(model, node, deferred, next, seen)) reached.Add(deferred.HelperKey);
                }
            }

            foreach (var deferred in pending.Where(d => !reached.Contains(d.HelperKey)))
                Unresolved.Add($"{deferred.File}:{deferred.Line} - no call site found for helper {deferred.HelperDisplay}");

            pending = next;
        }

        foreach (var deferred in pending)
            Unresolved.Add($"{deferred.File}:{deferred.Line} - indirection deeper than {maxHops} hops via {deferred.HelperDisplay}");
    }

    /// <summary>Returns true if this call site produced an event or another hop to follow.</summary>
    private bool ResolveOne(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        DeferredSend deferred,
        List<DeferredSend> next,
        HashSet<string> seen)
    {
        var nameArgument = ArgumentAt(invocation, deferred.NameOrdinal);
        if (nameArgument is null) return false;

        var location = invocation.GetLocation().GetLineSpan();
        var file = location.Path;
        var line = location.StartLinePosition.Line + 1;

        var schema = deferred.LocalSchema;
        var clrType = deferred.LocalClrType;
        var anonymous = deferred.LocalAnonymous;

        // Resolve the payload at this hop if the helper took it as a parameter.
        var payloadStillDeferred = deferred.PayloadOrdinal;
        if (deferred.PayloadOrdinal is { } ordinal)
        {
            var payloadArgument = ArgumentAt(invocation, ordinal);
            var payloadType = payloadArgument is null ? null : model.GetTypeInfo(payloadArgument.Expression).Type;

            if (payloadType is not null)
            {
                schema = schemas.Build(payloadType);
                clrType = payloadType.ToDisplayString();
                anonymous = payloadType.IsAnonymousType;
                payloadStillDeferred = ParameterOrdinal(model, payloadArgument!.Expression);
            }
        }

        if (model.GetConstantValue(nameArgument.Expression) is { HasValue: true, Value: string eventName })
        {
            if (schema is null) return false;

            Outbound.Add(new OutboundSite(eventName, clrType, anonymous, schema, file, line)
            {
                ViaHelper = deferred.HelperDisplay,
            });
            return true;
        }

        // Still a parameter - queue the enclosing method as the next hop.
        var outerOrdinal = ParameterOrdinal(model, nameArgument.Expression);
        var enclosing = EnclosingMethod(model, invocation);

        if (outerOrdinal is null || enclosing is null)
        {
            Unresolved.Add($"{file}:{line} - non-literal event name passed to {deferred.HelperDisplay}");
            return false;
        }

        var outerKey = Key(enclosing);
        if (!seen.Add(outerKey)) return true; // already queued or resolved; not an error

        next.Add(new DeferredSend(
            outerKey, enclosing.ToDisplayString(), outerOrdinal.Value, payloadStillDeferred,
            schema, clrType, anonymous, file, line));

        return true;
    }

    /// <summary>Positional or named argument at a parameter ordinal.</summary>
    private static ArgumentSyntax? ArgumentAt(InvocationExpressionSyntax invocation, int ordinal)
    {
        var arguments = invocation.ArgumentList.Arguments;
        return arguments.Count > ordinal && arguments[ordinal].NameColon is null
            ? arguments[ordinal]
            : arguments.FirstOrDefault(a => a.NameColon is not null);
    }

    /// <summary>The ordinal of the enclosing method's parameter this expression refers to, if any.</summary>
    private static int? ParameterOrdinal(SemanticModel model, ExpressionSyntax expression) =>
        model.GetSymbolInfo(expression).Symbol is IParameterSymbol parameter ? parameter.Ordinal : null;

    private static IMethodSymbol? EnclosingMethod(SemanticModel model, SyntaxNode node)
    {
        var declaration = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return declaration is null ? null : model.GetDeclaredSymbol(declaration) as IMethodSymbol;
    }

    /// <summary>Stable across compilations - the same helper seen from two projects must match.</summary>
    private static string Key(IMethodSymbol symbol) =>
        (symbol.ReducedFrom ?? symbol.OriginalDefinition).ToDisplayString();

    // ── Inbound ──────────────────────────────────────────────────────────────

    private void TryInbound(SemanticModel model, MethodDeclarationSyntax method)
    {
        if (model.GetDeclaredSymbol(method) is not IMethodSymbol symbol) return;

        var attribute = symbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "HubMethodNameAttribute");

        if (attribute is null || attribute.ConstructorArguments.Length == 0) return;
        if (attribute.ConstructorArguments[0].Value is not string eventName) return;

        var location = method.GetLocation().GetLineSpan();

        Inbound.Add(new InboundMethod(
            eventName,
            $"{symbol.ContainingType.Name}.{symbol.Name}",
            symbol.Parameters
                .Select(p => new InboundParameter(
                    SchemaBuilder.WireName(p.Name), p.Type.ToDisplayString(), schemas.Build(p.Type)))
                .ToList(),
            SummaryOf(symbol),
            location.Path,
            location.StartLinePosition.Line + 1));
    }

    /// <summary>The &lt;summary&gt; text from the XML doc comment, flattened to one line.</summary>
    private static string? SummaryOf(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;

        var start = xml.IndexOf("<summary>", StringComparison.Ordinal);
        var end = xml.IndexOf("</summary>", StringComparison.Ordinal);
        if (start < 0 || end < 0) return null;

        return string.Join(' ', xml[(start + "<summary>".Length)..end]
            .Split((char[])['\n', '\r', ' ', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
