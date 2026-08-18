using System.Net;
using System.Text.Json;

namespace Echo.Wiki;

/// <summary>What a lookup came back with, separating "no such page" from "nobody answered".</summary>
/// <param name="Value">The document, or null.</param>
/// <param name="ServiceReachable">False when the Guild service did not answer at all.</param>
public readonly record struct WikiFetch<T>(T? Value, bool ServiceReachable) where T : class
{
    /// <summary>Whether there is something to render.</summary>
    public bool Found => Value is not null;
}

/// <summary>
/// The wiki site's channel to the Guild service. The site is rendered here rather than in the
/// browser, so the anonymous origin never needs to reach the API itself and carries no script at
/// all - see <see cref="Echo.Sites.WikiSiteSecurity"/>.
/// </summary>
public sealed class WikiServiceClient(HttpClient http, ILogger<WikiServiceClient> logger)
{
    /// <summary>Where the Guild service answers, matching the gateway's own cluster address.</summary>
    public static string BaseAddress =>
        Environment.GetEnvironmentVariable("Services__Guild")
        ?? "http://guild.default.svc.cluster.local";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The index of a published wiki.</summary>
    /// <param name="slug">The wiki's published slug.</param>
    /// <param name="ct">Cancels with the request.</param>
    /// <returns>The wiki, or an empty result.</returns>
    public Task<WikiFetch<PublicWiki>> GetWikiAsync(string slug, CancellationToken ct) =>
        GetAsync<PublicWiki>($"/api/v1/wiki/public/{Uri.EscapeDataString(slug)}", ct);

    /// <summary>One published page.</summary>
    /// <param name="slug">The wiki's published slug.</param>
    /// <param name="pageSlug">The page's slug.</param>
    /// <param name="ct">Cancels with the request.</param>
    /// <returns>The page, or an empty result.</returns>
    public Task<WikiFetch<PublicWikiPage>> GetPageAsync(string slug, string pageSlug, CancellationToken ct) =>
        GetAsync<PublicWikiPage>(
            $"/api/v1/wiki/public/{Uri.EscapeDataString(slug)}/{Uri.EscapeDataString(pageSlug)}", ct);

    private async Task<WikiFetch<T>> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await http.GetAsync(path, ct);

            if (response.StatusCode == HttpStatusCode.NotFound) return new WikiFetch<T>(null, true);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "The guild service answered {Status} for the public wiki path {Path}.",
                    (int)response.StatusCode, path);

                return new WikiFetch<T>(null, false);
            }

            return new WikiFetch<T>(await response.Content.ReadFromJsonAsync<T>(Json, ct), true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(exception,
                "The guild service did not answer the public wiki path {Path} at {Address}.",
                path, BaseAddress);

            return new WikiFetch<T>(null, false);
        }
    }
}
