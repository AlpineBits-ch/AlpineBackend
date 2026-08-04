using Bots.Contracts.Gateway.Payloads;

namespace Unfurl.Contracts.Bus;

/// <summary>Resolve a batch of URLs into renderable embeds.</summary>
public class UnfurlUrlsRequest
{
    /// <summary>Absolute http/https URLs, already deduped and capped by the caller
    /// (<c>LinkExtractor</c>). The unfurler re-validates rather than trusting this.</summary>
    public List<string> Urls { get; set; } = [];

    /// <summary>Opaque correlation value echoed back untouched - Messaging puts the message id here
    /// so a late response can be matched to the message that asked for it.</summary>
    public string? CorrelationId { get; set; }
}

public class UnfurlUrlsResponse
{
    public List<UnfurlResult> Results { get; set; } = [];

    public string? CorrelationId { get; set; }
}

/// <summary>One URL's outcome.</summary>
public class UnfurlResult
{
    /// <summary>The URL as submitted, so the caller can match results to inputs positionally-free.</summary>
    public string Url { get; set; } = "";

    /// <summary>Null when the URL could not be turned into anything worth rendering.</summary>
    public EmbedPayload? Embed { get; set; }

    /// <summary>Set when <see cref="Embed"/> is null.</summary>
    public string? FailureReason { get; set; }
}
