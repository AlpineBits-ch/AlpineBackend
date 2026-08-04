using System.Net;
using AppEnvironment;
using AppEnvironment.Security;

namespace Unfurl.Application.Fetching;

public sealed record FetchResult(
    Uri FinalUrl,
    string? ContentType,
    byte[] Body,
    TimeSpan? OriginTtl);

public sealed class FetchException(string reason) : Exception(reason);

public static class UnfurlHttpClients
{
    /// <summary>Named client whose every connection is IP-checked and whose automatic redirect
    /// following is off - see <see cref="SafeFetcher"/>.</summary>
    public const string Outbound = "unfurl-outbound";
}

/// <summary>
/// The one place this service reaches out to the internet.
///
/// <para>Everything it fetches is a URL some user typed into a chat box, so it is written to the
/// assumption that the origin is hostile: it lies about its size, redirects inward, hangs, serves
/// gigabytes, or is the cloud metadata endpoint wearing a hostname.</para>
///
/// <para><b>Redirects are followed by hand.</b> The obvious alternative -
/// <c>AllowAutoRedirect = true</c> on a guarded handler - is not equivalent. The connect callback
/// would still vet each hop's IP, but nothing would re-check the <i>scheme</i> or the credentials
/// in a <c>Location</c> header, so a public page could bounce the request to
/// <c>file://</c> or to a URL carrying <c>user:password@</c>. Following them here runs the full
/// <see cref="OutboundAddressGuard.TryValidate"/> on every hop.</para>
/// </summary>
public class SafeFetcher(IHttpClientFactory httpClientFactory, ILogger<SafeFetcher> logger)
{
    private static readonly SemaphoreSlim GlobalLimit = new(Env.Unfurl.MaxConcurrentTotal);

    // One semaphore per origin host. Static because the limit is a property of the origin, not of a
    // request, and it must hold across every message being unfurled concurrently in this process.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> PerHostLimits = new();

    /// <summary>
    /// Fetches a document, following redirects manually and refusing to read more than
    /// <paramref name="maxBytes"/>.
    /// </summary>
    /// <param name="accept">Value for the Accept header, and the family of content types the caller
    /// is willing to receive.</param>
    public async Task<FetchResult> FetchAsync(string url, string accept, int maxBytes, CancellationToken ct)
    {
        var allowPrivate = Env.Unfurl.AllowPrivateTargets;

        if (!OutboundAddressGuard.TryValidate(url, allowPrivate, out var uri, out var error))
            throw new FetchException(error ?? "Invalid URL.");

        var client = httpClientFactory.CreateClient(UnfurlHttpClients.Outbound);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Env.Unfurl.FetchTimeout);

        await GlobalLimit.WaitAsync(timeout.Token);
        try
        {
            var current = uri!;

            for (var hop = 0; hop <= Env.Unfurl.MaxRedirects; hop++)
            {
                var perHost = PerHostLimits.GetOrAdd(current.Host, _ => new SemaphoreSlim(Env.Unfurl.MaxConcurrentPerHost));

                await perHost.WaitAsync(timeout.Token);
                HttpResponseMessage response;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, current);
                    request.Headers.TryAddWithoutValidation("Accept", accept);
                    request.Headers.TryAddWithoutValidation("Accept-Language", "en;q=0.9, *;q=0.5");

                    // ResponseHeadersRead so the size cap below applies while the body streams in,
                    // rather than after the whole thing is already buffered in memory.
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                }
                finally
                {
                    perHost.Release();
                }

                using (response)
                {
                    if (IsRedirect(response.StatusCode))
                    {
                        var location = response.Headers.Location;
                        if (location is null) throw new FetchException("Redirect without a Location header.");

                        // Relative Locations are legal and common.
                        var next = location.IsAbsoluteUri ? location : new Uri(current, location);

                        // The whole reason redirects are handled by hand: re-run every check.
                        if (!OutboundAddressGuard.TryValidate(next.ToString(), allowPrivate, out var validated, out var hopError))
                            throw new FetchException($"Refused redirect: {hopError}");

                        current = validated!;
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                        throw new FetchException($"Origin returned {(int)response.StatusCode}.");

                    var contentType = response.Content.Headers.ContentType?.MediaType;
                    var body = await ReadCappedAsync(response, maxBytes, timeout.Token);

                    return new FetchResult(current, contentType, body, ReadTtl(response));
                }
            }

            throw new FetchException("Too many redirects.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FetchException("Timed out.");
        }
        catch (HttpRequestException e)
        {
            // Covers the connect callback's refusal too, which is what a DNS-rebinding attempt or a
            // hostname pointing at 169.254.169.254 surfaces as.
            logger.LogDebug(e, "Fetch failed for {Url}", url);
            throw new FetchException("Could not reach the origin.");
        }
        finally
        {
            GlobalLimit.Release();
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> and throws if the origin keeps going.
    ///
    /// <para>Content-Length is deliberately not consulted as the limit. It is a claim by the
    /// origin, it is absent on every chunked response, and a hostile server sets it to 1 and then
    /// streams forever. Counting what actually arrives is the only cap that holds.</para>
    /// </summary>
    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();

        var chunk = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new FetchException("Response exceeded the size limit.");

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The origin's own opinion on how long this is good for, clamped. Honouring it is what makes a
    /// news front page refresh sooner than a static article, without us having to guess per site.
    /// </summary>
    private static TimeSpan? ReadTtl(HttpResponseMessage response)
    {
        var maxAge = response.Headers.CacheControl?.MaxAge;
        if (maxAge is null) return null;
        if (response.Headers.CacheControl?.NoStore == true) return TimeSpan.Zero;

        return maxAge;
    }
}
