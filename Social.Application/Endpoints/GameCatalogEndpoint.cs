using System.Globalization;
using System.IO.Compression;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;
using Wolverine.Http;

namespace Social.Api.Endpoints;

/// <summary>Serves the game-detection catalog to clients.</summary>
[Authorize]
public static class GameCatalogEndpoint
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The detection catalog, optionally narrowed to one platform.</summary>
    [WolverineGet("/api/v1/games/catalog")]
    public static async Task<IResult> GetCatalogAsync(
        HttpContext http,
        [NotBody] MicroserviceContext ctx,
        string? os = null,
        CancellationToken ct = default)
    {
        GamePlatform? platform = null;
        if (!string.IsNullOrWhiteSpace(os))
        {
            if (!TryParsePlatform(os, out var parsed))
                return Results.BadRequest($"os must be one of: {string.Join(", ", Enum.GetNames<GamePlatform>()).ToLowerInvariant()}");

            platform = parsed;
        }

        var executables = ctx.GameExecutables
            .AsNoTracking()
            .Where(e => e.GameApplication.IsEnabled);

        if (platform is not null) executables = executables.Where(e => e.Os == platform);

        // The ETag has to move when any contributing row moves, including a community entry that
        // never touches the seed version - so it is derived from the data, not from the seed
        // marker.
        var count = await executables.CountAsync(ct);
        var maxExecutableUpdated = await executables.MaxAsync(e => (DateTimeOffset?)e.UpdatedAt, ct);
        var maxApplicationUpdated = await executables.MaxAsync(e => (DateTimeOffset?)e.GameApplication.UpdatedAt, ct);

        var etag = ComputeETag(platform, count, maxExecutableUpdated, maxApplicationUpdated);

        // Revalidation is the common case by a wide margin: the catalog changes rarely and clients
        // ask on every launch.
        if (http.Request.Headers.IfNoneMatch.Any(v => string.Equals(v, etag, StringComparison.Ordinal)))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        var rows = await executables
            .Select(e => new
            {
                e.GameApplicationId,
                AppName = e.GameApplication.Name,
                e.GameApplication.DiscordApplicationId,
                e.Name,
                e.Os,
                e.IsLauncher,
                e.IsNegated,
            })
            .ToListAsync(ct);

        var games = rows
            .GroupBy(r => r.GameApplicationId)
            .Select(g => new CatalogGameDto
            {
                Id = g.Key,
                Name = g.First().AppName,
                ApplicationId = g.First().DiscordApplicationId,
                Rules = g.Select(r => new CatalogRuleDto
                {
                    Name = r.Name,
                    Os = r.Os.ToString().ToLowerInvariant(),
                    IsLauncher = r.IsLauncher,
                    IsNegated = r.IsNegated,
                }).ToList(),
            })
            .OrderBy(g => g.Id, StringComparer.Ordinal)
            .ToList();

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new CatalogDto { Version = etag, GameCount = games.Count, Games = games },
            SerializerOptions);

        http.Response.Headers.ETag = etag;
        // Must revalidate rather than serve blind from cache: a stale catalog silently reports the
        // wrong game rather than failing, which is the failure mode hardest for a user to notice.
        http.Response.Headers.CacheControl = "private, no-cache";
        http.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);

        // Compressed here rather than by global middleware, which this service does not configure -
        // roughly a megabyte of highly repetitive JSON, and adding response compression app-wide to
        // serve one endpoint would change the behaviour of every other one.
        if (AcceptsGzip(http.Request))
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            {
                await gzip.WriteAsync(payload, ct);
            }

            http.Response.Headers.ContentEncoding = "gzip";
            return Results.Bytes(buffer.ToArray(), MediaTypeNames.Application.Json);
        }

        return Results.Bytes(payload, MediaTypeNames.Application.Json);
    }

    private static bool AcceptsGzip(HttpRequest request) =>
        request.Headers.AcceptEncoding.Any(v => v is not null && v.Contains("gzip", StringComparison.OrdinalIgnoreCase));

    private static string ComputeETag(GamePlatform? platform, int count, DateTimeOffset? maxExecutable, DateTimeOffset? maxApplication)
    {
        // Application timestamps are folded in as well as executable ones: renaming a game changes
        // what the client renders without touching a single rule row.
        var latest = new[] { maxExecutable, maxApplication }.Max();

        var material = string.Create(CultureInfo.InvariantCulture,
            $"{platform?.ToString() ?? "all"}|{count}|{latest?.UtcTicks ?? 0}");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        return $"\"{Convert.ToHexStringLower(hash.AsSpan(0, 16))}\"";
    }

    private static bool TryParsePlatform(string os, out GamePlatform platform)
    {
        switch (os.Trim().ToLowerInvariant())
        {
            case "win32": platform = GamePlatform.Win32; return true;
            case "darwin": platform = GamePlatform.Darwin; return true;
            case "linux": platform = GamePlatform.Linux; return true;
            default: platform = default; return false;
        }
    }
}

public sealed class CatalogDto
{
    public required string Version { get; init; }
    public required int GameCount { get; init; }
    public required IReadOnlyList<CatalogGameDto> Games { get; init; }
}

public sealed class CatalogGameDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Present when the game announces an application id over the RPC socket, so a
    /// process match and an RPC connection for the same game resolve to one activity.</summary>
    public string? ApplicationId { get; init; }

    public required IReadOnlyList<CatalogRuleDto> Rules { get; init; }
}

public sealed class CatalogRuleDto
{
    /// <summary>Normalized: lower-case, forward slashes.</summary>
    public required string Name { get; init; }

    public required string Os { get; init; }

    /// <summary>The store front-end rather than the game. Loses to a non-launcher match.</summary>
    public required bool IsLauncher { get; init; }

    /// <summary>
    /// An exclusion: if this executable is running, the parent game is NOT a match.
    /// </summary>
    public required bool IsNegated { get; init; }
}
