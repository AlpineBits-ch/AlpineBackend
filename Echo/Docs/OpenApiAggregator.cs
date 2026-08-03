using System.Text.Json;
using System.Text.Json.Nodes;
using Yarp.ReverseProxy.Configuration;

namespace Echo.Docs;

/// <summary>
/// Builds one OpenAPI document for the whole public API by fetching each service's own document and
/// merging them.
/// </summary>
public sealed class OpenApiAggregator(
    IHttpClientFactory httpClientFactory,
    IProxyConfigProvider proxyConfig,
    ILogger<OpenApiAggregator> logger)
{
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cached;
    private DateTimeOffset _cachedAt;

    public async Task<string> GetDocumentAsync(CancellationToken ct = default)
    {
        if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < RefreshAfter) return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < RefreshAfter) return _cached;

            var document = await BuildAsync(ct);

            // Only replace a good document with another good one.
            if (document is not null)
            {
                _cached = document;
                _cachedAt = DateTimeOffset.UtcNow;
            }

            return _cached ?? EmptyDocument();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> BuildAsync(CancellationToken ct)
    {
        var paths = new JsonObject();
        var schemas = new JsonObject();
        var tags = new JsonArray();
        var reached = 0;

        foreach (var service in DocsCatalog.Services)
        {
            var source = await FetchAsync(service, ct);
            if (source is null) continue;

            reached++;
            tags.Add(new JsonObject
            {
                ["name"] = service.DisplayName,
                ["description"] = $"Served by the {service.Name} service.",
            });

            var skipped = Merge(service, source, paths, schemas);

            // A declared route the gateway does not expose is a genuine gap - either a missing proxy
            // route or a service-only endpoint. Logged rather than silently dropped.
            if (skipped.Count > 0)
            {
                logger.LogInformation(
                    "{Service}: {Count} declared routes are not reachable through the gateway and were "
                    + "left out of the docs: {Paths}",
                    service.Name, skipped.Count, string.Join(", ", skipped.Take(10)));
            }
        }

        if (reached == 0)
        {
            logger.LogWarning("No service returned an OpenAPI document; keeping any previously cached one");
            return null;
        }

        logger.LogInformation("Aggregated OpenAPI from {Reached}/{Total} services, {Paths} paths",
            reached, DocsCatalog.Services.Count, paths.Count);

        var merged = new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "venta.gg HTTP API",
                ["version"] = "v1",
                ["description"] =
                    "Aggregated from every service behind the gateway. Paths are the public ones - "
                    + "the gateway's service prefix is already applied.",
            },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = "https://venta.gg" }),
            ["tags"] = tags,
            ["paths"] = paths,
            ["components"] = new JsonObject { ["schemas"] = schemas },
        };

        return merged.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private async Task<JsonObject?> FetchAsync(DocsService service, CancellationToken ct)
    {
        var destination = ResolveDestination(service.Cluster);
        if (destination is null)
        {
            logger.LogWarning("No destination for cluster {Cluster}; skipping its docs", service.Cluster);
            return null;
        }

        try
        {
            using var client = httpClientFactory.CreateClient("docs");
            client.Timeout = TimeSpan.FromSeconds(10);

            var url = destination.TrimEnd('/') + service.DocumentPath;
            var json = await client.GetStringAsync(url, ct);

            return JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch OpenAPI from {Service}", service.Name);
            return null;
        }
    }

    /// <summary>First healthy destination address for a cluster, read from the live proxy config so
    /// the docs follow the same topology the traffic does.</summary>
    private string? ResolveDestination(string cluster) =>
        proxyConfig.GetConfig().Clusters
            .FirstOrDefault(c => string.Equals(c.ClusterId, cluster, StringComparison.OrdinalIgnoreCase))
            ?.Destinations?.Values.FirstOrDefault()?.Address;

    /// <summary>
    /// Copies one service's paths and schemas into the merged document, mapping each declared path
    /// to its public URL and namespacing the schema names.
    /// </summary>
    /// <returns>Declared paths the gateway does not expose, so the caller can report them.</returns>
    private static List<string> Merge(
        DocsService service, JsonObject source, JsonObject paths, JsonObject schemas)
    {
        var prefix = $"{service.DisplayName}.";
        var skipped = new List<string>();

        if (source["components"]?["schemas"] is JsonObject sourceSchemas)
        {
            foreach (var (name, schema) in sourceSchemas.ToList())
            {
                sourceSchemas.Remove(name);
                schemas[prefix + name] = Requalify(schema, prefix);
            }
        }

        if (source["paths"] is not JsonObject sourcePaths) return skipped;

        foreach (var (path, item) in sourcePaths.ToList())
        {
            sourcePaths.Remove(path);
            if (item is not JsonObject operations) continue;

            var publicPath = service.ToPublicPath(path);
            if (publicPath is null)
            {
                skipped.Add(path);
                continue;
            }

            foreach (var (_, operation) in operations)
            {
                if (operation is not JsonObject op) continue;
                // One tag per service so the sidebar groups by service rather than by controller.
                op["tags"] = new JsonArray(service.DisplayName);
            }

            paths[publicPath] = Requalify(operations, prefix);
        }

        return skipped;
    }

    /// <summary>Rewrites every <c>$ref</c> so it points at the namespaced schema name.</summary>
    private static JsonNode? Requalify(JsonNode? node, string prefix)
    {
        const string marker = "#/components/schemas/";

        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (key == "$ref" && value?.GetValue<string>() is { } reference
                                      && reference.StartsWith(marker, StringComparison.Ordinal))
                    {
                        obj[key] = marker + prefix + reference[marker.Length..];
                        continue;
                    }

                    obj[key] = Requalify(value?.DeepClone(), prefix);
                }
                return obj;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    array[i] = Requalify(array[i]?.DeepClone(), prefix);
                return array;

            default:
                return node;
        }
    }

    private static string EmptyDocument() => new JsonObject
    {
        ["openapi"] = "3.1.0",
        ["info"] = new JsonObject
        {
            ["title"] = "venta.gg HTTP API",
            ["version"] = "v1",
            ["description"] = "No service could be reached for its OpenAPI document.",
        },
        ["paths"] = new JsonObject(),
    }.ToJsonString();
}
