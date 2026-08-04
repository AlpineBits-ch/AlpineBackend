using Bots.Contracts.Gateway.Payloads;

namespace Unfurl.Contracts.Bus;

/// <summary>
/// Resolve a batch of URLs into renderable embeds. Sent by Messaging after a message is persisted -
/// never before, because a send must not wait on a third-party origin.
///
/// <para>A batch rather than one message per URL: a single chat message can carry several links and
/// they should land in one <c>MessageUpdated</c>, not one per link, or a client repaints the
/// message once per preview.</para>
/// </summary>
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

/// <summary>
/// One URL's outcome. A failure is a first-class result rather than an exception: one dead link in a
/// message of five must not cost the other four their previews.
/// </summary>
public class UnfurlResult
{
    /// <summary>The URL as submitted, so the caller can match results to inputs positionally-free.</summary>
    public string Url { get; set; } = "";

    /// <summary>Null when the URL could not be turned into anything worth rendering.</summary>
    public EmbedPayload? Embed { get; set; }

    /// <summary>Set when <see cref="Embed"/> is null. Diagnostic only - never surfaced to a user,
    /// since it can echo an attacker-chosen hostname.</summary>
    public string? FailureReason { get; set; }
}
