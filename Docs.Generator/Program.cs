using System.Text.Json;
using Docs.Generator;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;

// Must happen before any Roslyn/MSBuild type is touched.
if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();

var solutionPath = args.FirstOrDefault(a => a.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                   ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Echo.sln");

var solutionRoot = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
var outputDirectory = ArgValue("--out") ?? Path.Combine(solutionRoot, "docs", "generated");

Console.WriteLine($"solution : {Path.GetFullPath(solutionPath)}");
Console.WriteLine($"output   : {Path.GetFullPath(outputDirectory)}");
Console.WriteLine();

using var workspace = MSBuildWorkspace.Create();
workspace.WorkspaceFailed += (_, e) =>
{
    // Design-time build warnings are noisy and mostly harmless; only failures matter.
    if (e.Diagnostic.Kind == Microsoft.CodeAnalysis.WorkspaceDiagnosticKind.Failure)
        Console.Error.WriteLine($"  workspace: {e.Diagnostic.Message}");
};

Console.WriteLine("loading solution (design-time build, this takes a minute)...");
var solution = await workspace.OpenSolutionAsync(solutionPath);

var schemas = new SchemaBuilder();
var extractor = new RealtimeExtractor(schemas);

var compilations = new List<Microsoft.CodeAnalysis.Compilation>();

foreach (var project in solution.Projects)
{
    // Test projects contain fakes and assertions that would pollute the contract.
    if (project.Name.EndsWith(".Tests", StringComparison.Ordinal)) continue;
    if (project.Name == "Docs.Generator") continue;

    var compilation = await project.GetCompilationAsync();
    if (compilation is null) continue;

    compilations.Add(compilation);
    await extractor.ScanAsync(compilation);
}

// Fan-out helpers take the event name as a parameter; their real names live at the call sites.
await extractor.ResolveDeferredAsync(compilations);

Console.WriteLine();
Console.WriteLine($"outbound call sites : {extractor.Outbound.Count}");
Console.WriteLine($"distinct events     : {extractor.Outbound.Select(o => o.EventName).Distinct().Count()}");
Console.WriteLine($"anonymous payloads  : {extractor.Outbound.Count(o => o.PayloadIsAnonymous)}");
Console.WriteLine($"inbound hub methods : {extractor.Inbound.Count}");

// ── Conflicts ────────────────────────────────────────────────────────────────
// One event name carrying two different payload shapes is a client-facing bug, not a
// documentation inconvenience: a typed client cannot deserialise both.
var conflicts = extractor.Outbound
    .GroupBy(o => o.EventName, StringComparer.Ordinal)
    .Select(g => new
    {
        Event = g.Key,
        Shapes = g.GroupBy(o => Fingerprint(o.Schema), StringComparer.Ordinal).ToList(),
        Sites = g.ToList(),
    })
    .Where(x => x.Shapes.Count > 1)
    .OrderByDescending(x => x.Shapes.Count)
    .ToList();

Console.WriteLine($"via fan-out helper  : {extractor.Outbound.Count(o => o.ViaHelper is not null)}");
Console.WriteLine($"shape conflicts     : {conflicts.Count}");
Console.WriteLine($"unresolved          : {extractor.Unresolved.Count}");
Console.WriteLine();

if (extractor.Unresolved.Count > 0)
{
    Console.WriteLine("Could not be documented - these are holes in the contract, not noise:");
    foreach (var problem in extractor.Unresolved)
        Console.WriteLine($"  {problem.Replace(solutionRoot + Path.DirectorySeparatorChar, "")}");
    Console.WriteLine();
}

if (conflicts.Count > 0)
{
    Console.WriteLine("Events sent with more than one payload shape:");
    foreach (var conflict in conflicts)
    {
        Console.WriteLine($"  {conflict.Event}  ({conflict.Shapes.Count} shapes)");
        foreach (var site in conflict.Sites)
            Console.WriteLine($"      {Relative(site.File)}:{site.Line}  [{Fields(site.Schema)}]");
    }
    Console.WriteLine();
}

if (schemas.Truncated.Count > 0)
{
    Console.WriteLine($"Schemas cut off at max depth ({schemas.Truncated.Count}) - these are incomplete:");
    foreach (var type in schemas.Truncated.Order(StringComparer.Ordinal))
        Console.WriteLine($"  {type}");
    Console.WriteLine();
}

Directory.CreateDirectory(outputDirectory);

var inventory = new
{
    generatedFrom = "Roslyn static analysis of the solution source",
    outbound = extractor.Outbound
        .GroupBy(o => o.EventName, StringComparer.Ordinal)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => new
        {
            name = g.Key,
            shapeCount = g.GroupBy(o => Fingerprint(o.Schema), StringComparer.Ordinal).Count(),
            sites = g.Select(o => new
            {
                file = Relative(o.File),
                line = o.Line,
                clrType = o.PayloadClrType,
                anonymous = o.PayloadIsAnonymous,
                schema = o.Schema,
            }),
        }),
    inbound = extractor.Inbound
        .OrderBy(i => i.EventName, StringComparer.Ordinal)
        .Select(i => new
        {
            name = i.EventName,
            clrMethod = i.ClrMethod,
            summary = i.Summary,
            file = Relative(i.File),
            line = i.Line,
            parameters = i.Parameters.Select(p => new { p.Name, clrType = p.ClrType, schema = p.Schema }),
        }),
};

var inventoryPath = Path.Combine(outputDirectory, "realtime-inventory.json");
await File.WriteAllTextAsync(inventoryPath, JsonSerializer.Serialize(inventory, new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
}));

Console.WriteLine($"wrote {inventoryPath}");

var asyncApiPath = Path.Combine(outputDirectory, "asyncapi.json");
await File.WriteAllTextAsync(asyncApiPath, AsyncApiWriter.Write(extractor.Outbound, extractor.Inbound));
Console.WriteLine($"wrote {asyncApiPath}");

return 0;

string? ArgValue(string flag)
{
    var index = Array.IndexOf(args, flag);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

string Relative(string path) => Path.GetRelativePath(solutionRoot, path).Replace('\\', '/');

// Property names + types, order-insensitive: enough to tell two payload shapes apart.
static string Fingerprint(PayloadSchema node) =>
    node.Properties.Count == 0
        ? $"{node.Type}{(node.Items is null ? "" : $"[{Fingerprint(node.Items)}]")}"
        : string.Join(",", node.Properties.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}:{Fingerprint(p.Value)}"));

static string Fields(PayloadSchema node) =>
    node.Properties.Count == 0 ? node.ClrType ?? node.Type : string.Join(", ", node.Properties.Keys);
