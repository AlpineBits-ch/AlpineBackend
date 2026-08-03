using System.Text.Json;
using Docs.Generator;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;

// Must happen before any Roslyn/MSBuild type is touched.
if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();

var solutionPath = args.FirstOrDefault(a => a.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                   ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Echo.sln");

var solutionRoot = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;

// Default straight into the gateway's wwwroot: these artifacts are committed, so that the
// published docs are reviewable in a diff and the Docker build does not need a full-solution
// design-time build. --check verifies they are current instead of writing them.
var outputDirectory = ArgValue("--out") ?? Path.Combine(solutionRoot, "Echo", "wwwroot", "docs");
var checkOnly = args.Contains("--check");

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
var endpoints = new EndpointExtractor(schemas);

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
    await endpoints.ScanAsync(compilation, project.Name);
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

var stale = new List<string>();

await Emit("realtime-inventory.json", JsonSerializer.Serialize(inventory, new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
}));

await Emit("asyncapi.json", AsyncApiWriter.Write(extractor.Outbound, extractor.Inbound));

// ── HTTP response overlay ────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine($"endpoints           : {endpoints.Endpoints.Count}");
Console.WriteLine($"  with a 200 body   : {endpoints.Endpoints.Count(e => e.Responses.Any(r => r.Status == 200 && r.Body is not null))}");
Console.WriteLine($"  with XML summary  : {endpoints.Endpoints.Count(e => !string.IsNullOrWhiteSpace(e.Summary))}");
Console.WriteLine($"  unreadable        : {endpoints.Unresolved.Count}");

if (endpoints.Unresolved.Count > 0)
{
    // Not noise: each one is an endpoint whose responses will stay undocumented.
    foreach (var problem in endpoints.Unresolved.Take(15))
        Console.WriteLine($"    {problem}");
    if (endpoints.Unresolved.Count > 15)
        Console.WriteLine($"    ... and {endpoints.Unresolved.Count - 15} more");
}

await Emit("openapi-responses.json", ResponseOverlayWriter.Write(endpoints.Endpoints));

if (checkOnly && stale.Count > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Generated documentation is out of date:");
    foreach (var file in stale) Console.Error.WriteLine($"  {file}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Run: dotnet run --project Docs.Generator -- Echo.sln");
    return 1;
}

return 0;

// Writes an artifact, or under --check compares it against what is committed.
async Task Emit(string fileName, string content)
{
    var path = Path.Combine(outputDirectory, fileName);

    if (checkOnly)
    {
        var current = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
        if (Normalise(current) == Normalise(content))
        {
            Console.WriteLine($"up to date  {fileName}");
        }
        else
        {
            Console.WriteLine($"STALE       {fileName}");
            stale.Add(fileName);
        }
        return;
    }

    await File.WriteAllTextAsync(path, content);
    Console.WriteLine($"wrote       {path}");
}

// Line endings differ between a Windows checkout and a Linux CI runner; the content does not.
static string? Normalise(string? text) => text?.Replace("\r\n", "\n").TrimEnd();

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
