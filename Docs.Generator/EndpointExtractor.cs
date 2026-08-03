using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Docs.Generator;

/// <summary>One documented response of one endpoint.</summary>
internal sealed record EndpointResponse(int Status, PayloadSchema? Body, string? ClrType);

/// <summary>One Wolverine HTTP endpoint, as declared in source.</summary>
internal sealed record EndpointInfo(
    string Project,
    string Verb,
    string DeclaredPath,
    string ClrMethod,
    string? Summary,
    IReadOnlyList<EndpointResponse> Responses,
    string File,
    int Line);

/// <summary>
/// Recovers the response contract of every Wolverine HTTP endpoint from its body.
/// </summary>
internal sealed class EndpointExtractor(SchemaBuilder schemas)
{
    private static readonly Dictionary<string, string> Verbs = new(StringComparer.Ordinal)
    {
        ["WolverineGetAttribute"] = "get",
        ["WolverinePostAttribute"] = "post",
        ["WolverinePutAttribute"] = "put",
        ["WolverineDeleteAttribute"] = "delete",
        ["WolverinePatchAttribute"] = "patch",
    };

    /// <summary>Status code per <c>Results.X</c> factory. Anything absent is reported, not guessed.</summary>
    private static readonly Dictionary<string, int> StatusOf = new(StringComparer.Ordinal)
    {
        ["Ok"] = 200,
        ["Json"] = 200,
        ["Content"] = 200,
        ["Created"] = 201,
        ["Accepted"] = 202,
        ["NoContent"] = 204,
        ["Redirect"] = 302,
        ["BadRequest"] = 400,
        ["ValidationProblem"] = 400,
        ["Unauthorized"] = 401,
        ["Forbid"] = 403,
        ["NotFound"] = 404,
        ["Conflict"] = 409,
        ["Problem"] = 500,
        ["InternalServerError"] = 500,
        // Only ever used with an explicit code; recorded as 200 so the operation is not dropped,
        // and reported below so the real code can be pinned by hand if it matters.
        ["StatusCode"] = 200,
    };

    /// <summary>Factories whose first argument is the response body.</summary>
    private static readonly HashSet<string> HasBody =
        new(StringComparer.Ordinal) { "Ok", "Json", "Created", "Accepted", "BadRequest", "Conflict" };

    public List<EndpointInfo> Endpoints { get; } = [];
    public List<string> Unresolved { get; } = [];

    public async Task ScanAsync(Compilation compilation, string project)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync();

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                TryEndpoint(model, method, project);
        }
    }

    private void TryEndpoint(SemanticModel model, MethodDeclarationSyntax method, string project)
    {
        if (model.GetDeclaredSymbol(method) is not IMethodSymbol symbol) return;

        var attribute = symbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass is not null && Verbs.ContainsKey(a.AttributeClass.Name));

        if (attribute?.AttributeClass is null) return;
        if (attribute.ConstructorArguments.Length == 0) return;
        if (attribute.ConstructorArguments[0].Value is not string declaredPath) return;

        var location = method.GetLocation().GetLineSpan();
        var file = location.Path;
        var line = location.StartLinePosition.Line + 1;

        var responses = new Dictionary<int, EndpointResponse>();
        Collect(model, method, responses, file, line, depth: 0);

        if (responses.Count == 0)
        {
            // An endpoint that returns a typed value rather than IResult needs nothing from this
            // overlay - ASP.NET already infers its response schema from the signature.
            if (MentionsResult(symbol.ReturnType))
                Unresolved.Add($"{Short(file)}:{line} - no Results.* returns found in {symbol.Name}");
            return;
        }

        Endpoints.Add(new EndpointInfo(
            project,
            Verbs[attribute.AttributeClass.Name],
            declaredPath.StartsWith('/') ? declaredPath : "/" + declaredPath,
            $"{symbol.ContainingType.Name}.{symbol.Name}",
            SummaryOf(symbol),
            responses.Values.OrderBy(r => r.Status).ToList(),
            file,
            line));
    }

    /// <summary>
    /// Reads every <c>Results.X(...)</c> in a method body, following one level of delegation.
    /// </summary>
    private void Collect(
        SemanticModel model,
        SyntaxNode body,
        Dictionary<int, EndpointResponse> responses,
        string file,
        int line,
        int depth)
    {
        const int maxDepth = 2;

        foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax access)
            {
                if (depth < maxDepth) FollowDelegate(model, invocation, responses, file, line, depth);
                continue;
            }

            var receiver = access.Expression.ToString();
            if (receiver is not ("Results" or "TypedResults"))
            {
                if (depth < maxDepth) FollowDelegate(model, invocation, responses, file, line, depth);
                continue;
            }

            var factory = access.Name.Identifier.ValueText;
            if (!StatusOf.TryGetValue(factory, out var status))
            {
                Unresolved.Add($"{Short(file)}:{line} - unmapped result factory Results.{factory}");
                continue;
            }

            if (factory == "StatusCode")
                Unresolved.Add($"{Short(file)}:{line} - Results.StatusCode recorded as 200; real code is dynamic");

            PayloadSchema? payload = null;
            string? clrType = null;

            if (HasBody.Contains(factory) && invocation.ArgumentList.Arguments.Count > 0)
            {
                var expression = invocation.ArgumentList.Arguments[0].Expression;
                var type = model.GetTypeInfo(expression).Type;

                // A bare string to BadRequest/Conflict is an error message, not a DTO.
                if (type is not null && !(status >= 400 && type.SpecialType == SpecialType.System_String))
                {
                    payload = schemas.Build(type);
                    clrType = type.ToDisplayString();
                }
            }

            // Keep the richest description of each status: a later Results.Ok(dto) must not be
            // overwritten by an earlier bodyless one.
            if (!responses.TryGetValue(status, out var existing) || (existing.Body is null && payload is not null))
                responses[status] = new EndpointResponse(status, payload, clrType);
        }
    }

    /// <summary>Steps into a call to a method declared in this solution and keeps collecting.</summary>
    private void FollowDelegate(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        Dictionary<int, EndpointResponse> responses,
        string file,
        int line,
        int depth)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol target) return;

        var reference = target.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null) return;

        var declaration = reference.GetSyntax();
        if (declaration is not MethodDeclarationSyntax) return;

        // A different syntax tree needs its own semantic model.
        var targetModel = model.Compilation.ContainsSyntaxTree(declaration.SyntaxTree)
            ? model.Compilation.GetSemanticModel(declaration.SyntaxTree)
            : null;

        if (targetModel is null) return;

        Collect(targetModel, declaration, responses, file, line, depth + 1);
    }

    /// <summary>Whether the declared return type involves <c>IResult</c> at all - directly, wrapped
    /// in a Task, or as part of a Wolverine cascading-message tuple.</summary>
    private static bool MentionsResult(ITypeSymbol type) =>
        type.ToDisplayString().Contains("IResult", StringComparison.Ordinal);

    private static string Short(string path) => Path.GetFileName(path);

    private static string? SummaryOf(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;

        var start = xml.IndexOf("<summary>", StringComparison.Ordinal);
        var end = xml.IndexOf("</summary>", StringComparison.Ordinal);
        if (start < 0 || end < 0) return null;

        var text = string.Join(' ', xml[(start + "<summary>".Length)..end]
            .Split((char[])['\n', '\r', ' ', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        // Strip the XML doc tags we do not render; the text inside them is still useful.
        return text
            .Replace("<para>", " ").Replace("</para>", " ")
            .Replace("<c>", "`").Replace("</c>", "`")
            .Replace("<b>", "").Replace("</b>", "")
            .Replace("<em>", "").Replace("</em>", "")
            .Trim();
    }
}
