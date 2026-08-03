using System.Text.Json;
using System.Text.Json.Nodes;

namespace Docs.Generator;

/// <summary>Emits an AsyncAPI 3 document for the single realtime hub.</summary>
internal static class AsyncApiWriter
{
    public static string Write(IReadOnlyList<OutboundSite> outbound, IReadOnlyList<InboundMethod> inbound)
    {
        var channels = new JsonObject();
        var operations = new JsonObject();
        var messages = new JsonObject();

        foreach (var group in outbound.GroupBy(o => o.EventName, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var shapes = group.GroupBy(o => Fingerprint(o.Schema), StringComparer.Ordinal).ToList();
            var id = Identifier(group.Key);

            messages[id] = new JsonObject
            {
                ["name"] = group.Key,
                ["title"] = group.Key,
                ["summary"] = Summary(group.Key, shapes.Count, group.First()),
                ["payload"] = shapes.Count == 1
                    ? ToSchema(group.First().Schema)
                    : new JsonObject
                    {
                        // A union rather than a merge: merging would invent a shape no call site
                        // actually sends, which is worse than admitting the ambiguity.
                        ["oneOf"] = new JsonArray(shapes.Select(s => ToSchema(s.First().Schema)).ToArray<JsonNode?>()),
                        ["description"] = $"Sent with {shapes.Count} different payload shapes - see x-sites.",
                    },
                ["x-sites"] = new JsonArray(group
                    .Select(o => (JsonNode?)new JsonObject
                    {
                        ["file"] = o.File,
                        ["line"] = o.Line,
                        ["clrType"] = o.PayloadClrType,
                        ["anonymous"] = o.PayloadIsAnonymous,
                    })
                    .ToArray()),
            };

            operations[$"receive_{id}"] = new JsonObject
            {
                ["action"] = "receive",
                ["channel"] = new JsonObject { ["$ref"] = "#/channels/hub" },
                // AsyncAPI 3 requires an operation's messages to point at the *channel's* message
                // entries, not straight at components - referencing components validates as
                // "Operation message does not belong to the specified channel".
                ["messages"] = new JsonArray(new JsonObject { ["$ref"] = $"#/channels/hub/messages/{id}" }),
            };
        }

        foreach (var method in inbound.OrderBy(i => i.EventName, StringComparer.Ordinal))
        {
            var id = Identifier(method.EventName);

            // SignalR hub methods take a positional argument list, not a single object.
            var payload = method.Parameters.Count == 1
                ? ToSchema(method.Parameters[0].Schema)
                : new JsonObject
                {
                    ["type"] = "array",
                    ["prefixItems"] = new JsonArray(method.Parameters
                        .Select(p => (JsonNode?)ToSchema(p.Schema)).ToArray()),
                    ["description"] = "Positional hub-method arguments.",
                };

            messages[id] = new JsonObject
            {
                ["name"] = method.EventName,
                ["title"] = method.EventName,
                ["summary"] = method.Summary ?? $"Invoked by the client. Handled by {method.ClrMethod}.",
                ["payload"] = payload,
                ["x-source"] = new JsonObject { ["file"] = method.File, ["line"] = method.Line },
            };

            operations[$"send_{id}"] = new JsonObject
            {
                ["action"] = "send",
                ["channel"] = new JsonObject { ["$ref"] = "#/channels/hub" },
                // AsyncAPI 3 requires an operation's messages to point at the *channel's* message
                // entries, not straight at components - referencing components validates as
                // "Operation message does not belong to the specified channel".
                ["messages"] = new JsonArray(new JsonObject { ["$ref"] = $"#/channels/hub/messages/{id}" }),
            };
        }

        channels["hub"] = new JsonObject
        {
            ["address"] = "/api/v1/ws/hub",
            ["title"] = "Realtime hub",
            ["description"] =
                "The single per-user SignalR connection, terminated on the gateway. Authenticate with "
                + "?access_token=<jwt>; pass ?deviceId=<id> to address a single device. Server pushes "
                + "reach it over the Redis backplane from the owning microservice.",
            ["messages"] = new JsonObject(messages
                .Select(m => new KeyValuePair<string, JsonNode?>(
                    m.Key, new JsonObject { ["$ref"] = $"#/components/messages/{m.Key}" }))),
        };

        var document = new JsonObject
        {
            ["asyncapi"] = "3.0.0",
            ["info"] = new JsonObject
            {
                ["title"] = "venta.gg Realtime API",
                ["version"] = "1.0.0",
                // Rendered as the Introduction section by the docs page, so the shell around it
                // stays chrome-only and the renderer owns the whole reading area.
                ["description"] = string.Join("\n\n",
                    "One SignalR connection per user, terminated on the gateway at `/api/v1/ws/hub`.",
                    "Authenticate with `?access_token=<jwt>`. Pass `?deviceId=<id>` so the server can "
                    + "address a single device - used to hand a call or voice session over between a "
                    + "user's devices.",
                    "**receive** operations are pushed by the server. **send** operations are hub "
                    + "methods the client invokes.",
                    "Field names below are the wire names. SignalR's JSON protocol serialises with a "
                    + "camelCase policy and none of the `AddJsonProtocol` registrations override it, so "
                    + "a C# property `ChannelId` arrives as `channelId`.",
                    "Generated from source by Docs.Generator - every entry is a real call site."),
            },
            ["servers"] = new JsonObject
            {
                ["production"] = new JsonObject
                {
                    ["host"] = "venta.gg",
                    ["protocol"] = "wss",
                    ["description"] = "SignalR over WebSockets.",
                },
            },
            ["channels"] = channels,
            ["operations"] = operations,
            ["components"] = new JsonObject { ["messages"] = messages },
        };

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Summary(string eventName, int shapeCount, OutboundSite first) =>
        shapeCount > 1
            ? $"Pushed by the server. WARNING: {shapeCount} different payload shapes are sent under this name."
            : first.PayloadIsAnonymous
                ? "Pushed by the server. Payload is an anonymous object - shape derived from the call site."
                : $"Pushed by the server. Payload: {first.PayloadClrType}.";

    /// <summary>Our internal node shape to a JSON Schema object.</summary>
    private static JsonObject ToSchema(PayloadSchema node)
    {
        var schema = new JsonObject { ["type"] = node.Nullable ? Nullable(node.Type) : node.Type };

        if (node.Format is not null) schema["format"] = node.Format;
        if (node.Enum is not null) schema["enum"] = new JsonArray(node.Enum.Select(e => (JsonNode?)e).ToArray());
        if (node.Items is not null) schema["items"] = ToSchema(node.Items);
        if (node.ClrType is not null) schema["x-clr-type"] = node.ClrType;
        if (node.Note is not null) schema["description"] = node.Note;

        if (node.Properties.Count > 0)
        {
            schema["properties"] = new JsonObject(node.Properties
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, ToSchema(p.Value))));
        }

        return schema;
    }

    private static JsonNode Nullable(string type) => new JsonArray(type, "null");

    private static string Identifier(string eventName) => eventName.Replace('.', '_');

    private static string Fingerprint(PayloadSchema node) =>
        node.Properties.Count == 0
            ? $"{node.Type}{(node.Items is null ? "" : $"[{Fingerprint(node.Items)}]")}"
            : string.Join(",", node.Properties.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}:{Fingerprint(p.Value)}"));
}
