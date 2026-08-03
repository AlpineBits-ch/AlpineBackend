using System.Text.Json;
using System.Text.Json.Nodes;

namespace Echo.Docs;

/// <summary>
/// The response schemas Docs.Generator recovered from source, applied to the aggregated document.
///
/// <para><b>Why an overlay and not endpoint metadata.</b> 267 endpoints return
/// <c>Task&lt;IResult&gt;</c>, from which ASP.NET's OpenAPI generator can infer no response schema at
/// all - so the services' own documents describe every operation with empty responses. The
/// alternative is migrating all of them to typed results by hand. Instead the generator reads the
/// <c>Results.Ok(...)</c> calls already present in each body and emits this file, which is merged in
/// here. No endpoint changes; the docs gain 191 response bodies.</para>
///
/// <para><b>Drift is possible and is handled by doing nothing.</b> The overlay is generated at build
/// time from source while the documents are fetched from running services. An entry that matches
/// nothing is ignored, and an operation with no entry keeps whatever the service said. The overlay
/// only ever adds responses that the service did not describe - it never overwrites one that did.</para>
/// </summary>
public sealed class ResponseOverlay
{
    private readonly Dictionary<(string Project, string Verb, string Path), JsonObject> _byOperation = new();

    public int Count => _byOperation.Count;

    private ResponseOverlay() { }

    public static ResponseOverlay Empty { get; } = new();

    public static ResponseOverlay Load(string path, ILogger logger)
    {
        if (!File.Exists(path))
        {
            logger.LogWarning(
                "No response overlay at {Path}; the HTTP reference will list operations without "
                + "response bodies. Run Docs.Generator during the build to produce it.", path);
            return Empty;
        }

        try
        {
            var overlay = new ResponseOverlay();
            var document = JsonNode.Parse(File.ReadAllText(path));

            foreach (var operation in document?["operations"]?.AsArray() ?? [])
            {
                if (operation is not JsonObject entry) continue;

                var project = entry["project"]?.GetValue<string>();
                var verb = entry["verb"]?.GetValue<string>();
                var declared = entry["path"]?.GetValue<string>();
                if (project is null || verb is null || declared is null) continue;

                overlay._byOperation[(project, verb.ToLowerInvariant(), declared)] = entry;
            }

            logger.LogInformation("Loaded response overlay: {Count} operations", overlay.Count);
            return overlay;
        }
        catch (Exception ex)
        {
            // Bad docs are better than a gateway that will not start.
            logger.LogError(ex, "Could not read the response overlay at {Path}; continuing without it", path);
            return Empty;
        }
    }

    /// <summary>
    /// Fills in responses and the summary for one operation, if the generator saw it.
    /// </summary>
    public void Apply(DocsService service, string declaredPath, string verb, JsonObject operation)
    {
        if (_byOperation.Count == 0) return;

        var path = declaredPath.StartsWith('/') ? declaredPath : "/" + declaredPath;
        if (!_byOperation.TryGetValue((service.Project, verb.ToLowerInvariant(), path), out var entry)) return;

        if (entry["responses"] is JsonObject generated)
        {
            var responses = operation["responses"] as JsonObject;
            if (responses is null)
            {
                responses = new JsonObject();
                operation["responses"] = responses;
            }

            foreach (var (status, generatedResponse) in generated)
            {
                // Only fill gaps - an endpoint migrated to typed results describes itself better
                // than this analysis of its body ever could, so a real schema always wins.
                if (responses.ContainsKey(status) && DescribesABody(responses[status])) continue;
                responses[status] = generatedResponse?.DeepClone();
            }
        }

        // Wolverine synthesises summary/description/operationId from the route
        // ("GET_api_v1_inbox_unread") for every endpoint. That is not a description, and leaving it
        // in place would suppress the hand-written XML doc comment for the sake of a placeholder.
        if (entry["summary"] is { } summary && IsPlaceholder(operation["summary"], operation["operationId"]))
            operation["summary"] = summary.DeepClone();

        if (entry["description"] is { } description && IsPlaceholder(operation["description"], operation["operationId"]))
            operation["description"] = description.DeepClone();
    }

    /// <summary>
    /// A readable label for an operation the generator had nothing to say about.
    ///
    /// Wolverine's synthesised <c>GET_api_v1_guilds_guildId_automod</c> is what renderers show in
    /// the navigation, and a sidebar of those is unreadable. Only 69 of 272 endpoints carry an XML
    /// doc comment, so without this most of the reference would look like that.
    /// </summary>
    public static void EnsureReadableSummary(JsonObject operation, string verb, string publicPath)
    {
        const int maxLabel = 90;

        if (IsPlaceholder(operation["summary"], operation["operationId"]))
        {
            operation["summary"] = $"{verb.ToUpperInvariant()} {publicPath}";

            // The same filler is copied into description; leaving it there just repeats the label.
            if (IsPlaceholder(operation["description"], operation["operationId"]))
                operation.Remove("description");

            return;
        }

        // Now that GenerateDocumentationFile is on, .NET's OpenAPI generator puts the *whole* XML
        // <summary> here - and several of these are multi-paragraph explanations of why an endpoint
        // exists. That is worth reading in the body and unreadable in a sidebar, so the long form
        // moves to description and the label keeps the first sentence.
        var text = operation["summary"]!.GetValue<string>().Trim();
        if (text.Length <= maxLabel) return;

        if (operation["description"] is null || IsPlaceholder(operation["description"], operation["operationId"]))
            operation["description"] = text;

        operation["summary"] = Shorten(text, maxLabel);
    }

    private static string Shorten(string text, int max)
    {
        var stop = text.IndexOf(". ", StringComparison.Ordinal);
        if (stop > 0 && stop <= max) return text[..stop];

        var cut = text.LastIndexOf(' ', Math.Min(max, text.Length - 1));
        return text[..(cut > 40 ? cut : max)].TrimEnd(',', ';', ':', '-', ' ') + "…";
    }

    /// <summary>
    /// Whether a response really describes a body.
    ///
    /// <para>Every operation in these services arrives carrying
    /// <c>"$ref": "#/components/schemas/IResult"</c> - the generator reflecting the declared
    /// <c>IResult</c> return type, which says nothing about the payload. Treating that as a
    /// description would suppress the overlay on essentially every endpoint, which is exactly
    /// what it did before this check existed.</para>
    /// </summary>
    private static bool DescribesABody(JsonNode? response)
    {
        if (response is not JsonObject obj) return false;
        if (obj["content"] is not JsonObject content || content.Count == 0) return false;

        foreach (var (_, media) in content)
        {
            if (media?["schema"] is not JsonObject schema) continue;

            var reference = schema["$ref"]?.GetValue<string>();
            if (reference is not null)
            {
                if (reference.EndsWith("/IResult", StringComparison.Ordinal)) continue;
                return true;
            }

            if (schema["properties"] is JsonObject { Count: > 0 }) return true;
            if (schema["items"] is not null) return true;
            if (schema["allOf"] is not null || schema["oneOf"] is not null || schema["anyOf"] is not null) return true;
        }

        return false;
    }

    /// <summary>Wolverine's route-derived filler, which equals the operationId.</summary>
    private static bool IsPlaceholder(JsonNode? value, JsonNode? operationId)
    {
        if (value is null) return true;

        var text = value.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text)) return true;

        return operationId is not null
               && string.Equals(text, operationId.GetValue<string>(), StringComparison.Ordinal);
    }
}
