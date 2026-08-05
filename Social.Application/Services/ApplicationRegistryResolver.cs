using System.Net;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;

namespace Social.Api.Services;

/// <summary>
/// Learns the display name of an application id the bootstrap catalog does not contain, once, and
/// keeps it.
/// </summary>
public sealed class ApplicationRegistryResolver(
    MicroserviceContext ctx,
    IHttpClientFactory httpClientFactory,
    IDistributedCache cache,
    ILogger<ApplicationRegistryResolver> logger)
{
    /// <summary>The named client, configured in <c>Program.cs</c> with the timeout and user agent.</summary>
    public const string HttpClientName = "application-registry";

    /// <summary>How long a failed resolution is remembered.</summary>
    private static readonly TimeSpan NegativeCacheDuration = TimeSpan.FromHours(6);

    /// <summary>Names longer than this are not names.</summary>
    private const int MaxNameLength = 128;

    private static string NegativeCacheKey(string applicationId) => $"appregistry:miss:{applicationId}";

    /// <summary>
    /// Resolves and stores the name for <paramref name="applicationId"/>, returning it, or
    /// <c>null</c> if it could not be resolved.
    /// </summary>
    public async Task<string?> ResolveAndStoreAsync(string? applicationId, CancellationToken ct = default)
    {
        if (!GameCatalogLookup.IsWellFormedApplicationId(applicationId)) return null;

        // Re-checked rather than trusted from the caller: between their miss and this call another
        // request may have stored the same id, and this is the cheap query that avoids a duplicate
        // insert racing its own unique index.
        var existing = await ctx.GameApplications
            .AsNoTracking()
            .Where(g => g.DiscordApplicationId == applicationId && g.IsEnabled)
            .Select(g => g.Name)
            .FirstOrDefaultAsync(ct);

        if (existing is not null) return existing;

        if (await cache.GetStringAsync(NegativeCacheKey(applicationId!), ct) is not null) return null;

        var name = await FetchNameAsync(applicationId!, ct);

        if (name is null)
        {
            await RememberMissAsync(applicationId!, ct);
            return null;
        }

        return await StoreAsync(applicationId!, name, ct);
    }

    /// <summary>Asks the public application endpoint what this id is called.</summary>
    private async Task<string?> FetchNameAsync(string applicationId, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);

            // The id is validated as digits-only above, so it cannot escape the path segment.
            using var response = await client.GetAsync($"applications/{applicationId}/rpc", ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // A real answer: no such application.
                logger.LogDebug("Application {ApplicationId} is not a registered application.", applicationId);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Application registry lookup for {ApplicationId} returned {StatusCode}.",
                    applicationId, (int)response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<RegistryApplication>(ct);

            var name = ActivityWriteGuard.Clean(payload?.Name, MaxNameLength);

            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller went away. Not a miss - deliberately not cached as one.
            throw;
        }
        catch (Exception ex)
        {
            // Includes the timeout, which surfaces as a TaskCanceledException with an untriggered
            // token and would otherwise be mistaken for the caller cancelling.
            logger.LogInformation(ex, "Application registry lookup for {ApplicationId} failed.", applicationId);
            return null;
        }
    }

    /// <summary>
    /// Writes the learned row, tolerating the race where another request wrote it first.
    /// </summary>
    private async Task<string?> StoreAsync(string applicationId, string name, CancellationToken ct)
    {
        try
        {
            ctx.GameApplications.Add(new GameApplication
            {
                // Prefixed ids are assigned by the application, not the database - same as the
                // seeder does.
                Id = GameApplication.GenerateId(),
                DiscordApplicationId = applicationId,
                Name = name,

                // No executables, and that is the point: this row answers "what is this id called",
                // it is not something process scanning can find.
                Source = GameCatalogSource.Resolved,
                IsEnabled = true,
            });

            await ctx.SaveChangesAsync(ct);

            logger.LogInformation(
                "Learned application {ApplicationId} as {Name} from the application registry.",
                applicationId, name);

            return name;
        }
        catch (DbUpdateException ex)
        {
            // The unique index on DiscordApplicationId did its job: two users started the same
            // unknown application at once. The other writer's name is as good as ours.
            ctx.ChangeTracker.Clear();

            var winner = await ctx.GameApplications
                .AsNoTracking()
                .Where(g => g.DiscordApplicationId == applicationId && g.IsEnabled)
                .Select(g => g.Name)
                .FirstOrDefaultAsync(ct);

            if (winner is null)
            {
                logger.LogWarning(ex, "Could not store application {ApplicationId}.", applicationId);
            }

            return winner;
        }
    }

    private async Task RememberMissAsync(string applicationId, CancellationToken ct)
    {
        try
        {
            await cache.SetStringAsync(
                NegativeCacheKey(applicationId),
                "1",
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = NegativeCacheDuration },
                ct);
        }
        catch (Exception ex)
        {
            // An unreachable cache costs repeat lookups, not correctness.
            logger.LogDebug(ex, "Could not cache the registry miss for {ApplicationId}.", applicationId);
        }
    }

    /// <summary>The single field we read.</summary>
    private sealed record RegistryApplication
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
    }
}
