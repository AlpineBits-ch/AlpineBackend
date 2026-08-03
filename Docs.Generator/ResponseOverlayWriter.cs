using System.Text.Json;
using System.Text.Json.Nodes;

namespace Docs.Generator;

/// <summary>
/// Writes the response-schema overlay the gateway merges into the aggregated OpenAPI document.
///
/// <para>Keyed by (project, verb, declared path) - the path as the service declares it, before the
/// gateway's prefix rewrite - because that is what the service's own OpenAPI document contains. The
/// aggregator matches on it while the operation is still in the service's own coordinate system, and
/// rewrites the path afterwards.</para>
/// </summary>
internal static class ResponseOverlayWriter
{
    public static string Write(IReadOnlyList<EndpointInfo> endpoints)
    {
        var operations = new JsonArray();

        foreach (var endpoint in endpoints
                     .OrderBy(e => e.Project, StringComparer.Ordinal)
                     .ThenBy(e => e.DeclaredPath, StringComparer.Ordinal)
                     .ThenBy(e => e.Verb, StringComparer.Ordinal))
        {
            var responses = new JsonObject();

            foreach (var response in endpoint.Responses)
            {
                var entry = new JsonObject { ["description"] = Describe(response.Status) };

                if (response.Body is not null)
                {
                    entry["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject
                        {
                            ["schema"] = SchemaJson.ToJson(response.Body),
                        },
                    };
                    if (response.ClrType is not null) entry["x-clr-type"] = response.ClrType;
                }

                responses[response.Status.ToString()] = entry;
            }

            var operation = new JsonObject
            {
                ["project"] = endpoint.Project,
                ["verb"] = endpoint.Verb,
                ["path"] = endpoint.DeclaredPath,
                ["clrMethod"] = endpoint.ClrMethod,
                ["responses"] = responses,
                ["x-source"] = new JsonObject
                {
                    ["file"] = endpoint.File,
                    ["line"] = endpoint.Line,
                },
            };

            if (!string.IsNullOrWhiteSpace(endpoint.Summary))
            {
                // Two fields on purpose. Renderers use `summary` as the navigation label, so a
                // 400-character doc comment there produces a sidebar entry that is a paragraph;
                // the full text belongs in `description`, which is rendered in the body.
                operation["summary"] = FirstSentence(endpoint.Summary);
                operation["description"] = endpoint.Summary;
            }

            operations.Add(operation);
        }

        return new JsonObject
        {
            ["generatedFrom"] = "Roslyn analysis of Results.* returns in each endpoint body",
            ["operations"] = operations,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// A navigation-length label: the first sentence, hard-capped.
    ///
    /// The cap is not cosmetic - several of these doc comments are multi-paragraph explanations of
    /// why an endpoint exists, and the first sentence is reliably the what.
    /// </summary>
    private static string FirstSentence(string summary)
    {
        const int max = 90;

        var text = summary.Trim();
        var stop = text.IndexOf(". ", StringComparison.Ordinal);
        if (stop < 0 && text.EndsWith('.')) stop = text.Length - 1;
        if (stop > 0) text = text[..stop];

        if (text.Length <= max) return text;

        // Cut on a word boundary rather than mid-word.
        var cut = text.LastIndexOf(' ', max - 1);
        return text[..(cut > 40 ? cut : max - 1)].TrimEnd(',', ';', ':', '-') + "…";
    }

    private static string Describe(int status) => status switch
    {
        200 => "Success",
        201 => "Created",
        202 => "Accepted",
        204 => "Success, no content",
        302 => "Redirect",
        400 => "Invalid request",
        401 => "Not authenticated",
        403 => "Not permitted",
        404 => "Not found",
        409 => "Conflict with current state",
        500 => "Server error",
        _ => "Response",
    };
}

/// <summary>Shared conversion from our internal schema node to JSON Schema.</summary>
internal static class SchemaJson
{
    public static JsonObject ToJson(PayloadSchema node)
    {
        var schema = new JsonObject
        {
            ["type"] = node.Nullable ? new JsonArray(node.Type, "null") : node.Type,
        };

        if (node.Format is not null) schema["format"] = node.Format;
        if (node.Enum is not null) schema["enum"] = new JsonArray(node.Enum.Select(e => (JsonNode?)e).ToArray());
        if (node.Items is not null) schema["items"] = ToJson(node.Items);
        if (node.ClrType is not null) schema["x-clr-type"] = node.ClrType;
        if (node.Note is not null) schema["description"] = node.Note;

        if (node.Properties.Count > 0)
        {
            schema["properties"] = new JsonObject(node.Properties
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, ToJson(p.Value))));
        }

        return schema;
    }
}
