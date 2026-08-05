using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Social.Infrastructure.Seed;

/// <summary>The bootstrap artifact's on-disk shape. See <c>Seed/build-seed.mjs</c> for provenance.</summary>
public sealed record GameCatalogSeed
{
    [JsonPropertyName("version")] public string Version { get; init; } = null!;
    [JsonPropertyName("appCount")] public int AppCount { get; init; }
    [JsonPropertyName("apps")] public IReadOnlyList<SeedApp> Apps { get; init; } = [];
}

public sealed record SeedApp
{
    [JsonPropertyName("discordApplicationId")] public string DiscordApplicationId { get; init; } = null!;
    [JsonPropertyName("name")] public string Name { get; init; } = null!;
    [JsonPropertyName("aliases")] public string[]? Aliases { get; init; }
    [JsonPropertyName("executables")] public SeedExecutable[]? Executables { get; init; }

    /// <summary>Store SKUs by distributor.</summary>
    [JsonPropertyName("stores")] public Dictionary<string, string[]>? Stores { get; init; }
}

public sealed record SeedExecutable
{
    [JsonPropertyName("name")] public string Name { get; init; } = null!;
    [JsonPropertyName("os")] public string Os { get; init; } = null!;
    [JsonPropertyName("isLauncher")] public bool IsLauncher { get; init; }
    [JsonPropertyName("negated")] public bool Negated { get; init; }
}

/// <summary>Reads the embedded, gzipped bootstrap artifact.</summary>
public static class GameCatalogSeedReader
{
    private const string ResourceName = "Social.Infrastructure.Seed.game-catalog.seed.json.gz";

    public static GameCatalogSeed Read()
    {
        var assembly = Assembly.GetExecutingAssembly();

        using var compressed = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded seed '{ResourceName}' is missing. Available: {string.Join(", ", assembly.GetManifestResourceNames())}");

        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);

        return JsonSerializer.Deserialize<GameCatalogSeed>(gzip)
            ?? throw new InvalidOperationException("Embedded seed deserialized to null.");
    }
}
