using Bots.Contracts.Gateway.Payloads;
using Unfurl.Application.Services;
using Unfurl.Contracts.Bus;
using Wolverine.Attributes;

namespace Unfurl.Application.Handlers;

/// <summary>The service's only bus entry point.</summary>
[NonTransactional]
public class UnfurlUrlsHandler
{
    public static async Task<UnfurlUrlsResponse> Handle(
        UnfurlUrlsRequest request,
        UnfurlService unfurl,
        ILogger<UnfurlUrlsHandler> logger,
        CancellationToken ct)
    {
        // Cap defensively.
        var urls = request.Urls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(EmbedLimits.MaxUnfurledPerMessage)
            .ToList();

        // Resolved concurrently: the links in one message are independent, and a batch of five that
        // waits for each in turn takes five timeouts to fail rather than one.
        var results = await Task.WhenAll(urls.Select(url => unfurl.ResolveAsync(url, ct)));

        logger.LogDebug("Unfurled {Succeeded}/{Total} URLs for {CorrelationId}",
            results.Count(r => r.Embed is not null), results.Length, request.CorrelationId);

        return new UnfurlUrlsResponse
        {
            Results = results.ToList(),
            CorrelationId = request.CorrelationId,
        };
    }
}
