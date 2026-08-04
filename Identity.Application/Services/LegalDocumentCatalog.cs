using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppEnvironment;
using Identity.Domain.Enums;

namespace Identity.Application.Services;

/// <summary>One document version as declared in <c>docs/legal/manifest.json</c>.</summary>
public sealed record LegalDocumentFile(
    LegalDocumentType DocumentType,
    string Version,
    DateTimeOffset EffectiveAt,
    string FileName,
    string ContentHash);

/// <summary>Reads the on-disk legal documents and hashes them (T1-12).</summary>
public class LegalDocumentCatalog(ILogger<LegalDocumentCatalog> logger)
{
    private sealed class Manifest
    {
        [JsonPropertyName("documents")]
        public List<ManifestEntry> Documents { get; set; } = [];
    }

    private sealed class ManifestEntry
    {
        [JsonPropertyName("documentType")] public string DocumentType { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("effectiveAt")] public DateTimeOffset EffectiveAt { get; set; }
        [JsonPropertyName("file")] public string File { get; set; } = "";
    }

    private const string ManifestFileName = "manifest.json";

    /// <summary>Root the documents are read from.</summary>
    public string DirectoryPath { get; init; } = Env.Legal.DirectoryPath;

    /// <summary>Base URL stamped onto each row's <c>Url</c>.</summary>
    public string PublicBaseUrl { get; init; } = Env.Legal.PublicBaseUrl;

    /// <summary>Every declared document version, hashed.</summary>
    public IReadOnlyList<LegalDocumentFile> Load()
    {
        var manifestPath = Path.Combine(DirectoryPath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            logger.LogWarning(
                "No legal-document manifest at {Path}; no legal documents will be published and no "
                + "account will be asked to consent to anything.", manifestPath);
            return [];
        }

        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath));
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Legal-document manifest at {Path} is not valid JSON", manifestPath);
            return [];
        }

        var results = new List<LegalDocumentFile>();
        foreach (var entry in manifest?.Documents ?? [])
        {
            if (!Enum.TryParse<LegalDocumentType>(entry.DocumentType, ignoreCase: true, out var type))
            {
                logger.LogError("Legal-document manifest names unknown document type {Type}; skipped",
                    entry.DocumentType);
                continue;
            }

            // The manifest names a file, and only a file.
            if (string.IsNullOrWhiteSpace(entry.File)
                || entry.File != Path.GetFileName(entry.File))
            {
                logger.LogError("Legal-document manifest entry {Type}/{Version} names an unusable "
                    + "file {File}; skipped", entry.DocumentType, entry.Version, entry.File);
                continue;
            }

            var path = Path.Combine(DirectoryPath, entry.File);
            if (!File.Exists(path))
            {
                logger.LogError("Legal document {Type} v{Version} declares file {File}, which does "
                    + "not exist; skipped", type, entry.Version, entry.File);
                continue;
            }

            results.Add(new LegalDocumentFile(
                type,
                entry.Version,
                entry.EffectiveAt,
                entry.File,
                HashOf(File.ReadAllBytes(path))));
        }

        return results;
    }

    /// <summary>The bytes served for one version, or null when the manifest does not declare it.
    /// Reads through the manifest rather than composing a path from the route so a version string
    /// from a request can never address a file that was not published.</summary>
    public byte[]? ReadContent(LegalDocumentType type, string version)
    {
        var match = Load().FirstOrDefault(
            d => d.DocumentType == type && string.Equals(d.Version, version, StringComparison.Ordinal));

        return match is null ? null : File.ReadAllBytes(Path.Combine(DirectoryPath, match.FileName));
    }

    /// <summary>The address <c>LegalDocument.Url</c> carries for a version.</summary>
    public string UrlFor(LegalDocumentType type, string version) =>
        $"{PublicBaseUrl.TrimEnd('/')}/{type.ToString().ToLowerInvariant()}/{Uri.EscapeDataString(version)}";

    public static string HashOf(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
