using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Bots.Contracts.Gateway.Payloads;

namespace Messaging.Domain.Previews;

/// <summary>Finds the links in a message that should get a preview.</summary>
public static class LinkExtractor
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .Build();

    /// <summary>
    /// The URLs in <paramref name="content"/> that should be unfurled: absolute http/https only,
    /// deduped, in first-appearance order, capped at
    /// <see cref="EmbedLimits.MaxUnfurledPerMessage"/>.
    /// </summary>
    public static List<string> Extract(string? content)
    {
        // Short-circuit before parsing.
        if (string.IsNullOrWhiteSpace(content)) return [];
        if (!content.Contains("://", StringComparison.Ordinal)) return [];

        var document = Markdown.Parse(content, Pipeline);
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Code exclusion is structural rather than a check in this loop, and worth being explicit
        // about because it looks like an omission: Markdig does not run inline parsing inside
        // fenced or indented code blocks, and a code span is a leaf that cannot contain a link.
        foreach (var link in document.Descendants<LinkInline>())
        {
            // Markdig marks <https://…> as an autolink.
            if (link.IsAutoLink && WasAngleBracketed(content, link)) continue;

            if (!TryNormalize(link.Url, out var normalized)) continue;
            if (!seen.Add(normalized)) continue;

            found.Add(normalized);
            if (found.Count == EmbedLimits.MaxUnfurledPerMessage) break;
        }

        return found;
    }

    /// <summary>Convenience for the message store, whose content is UTF-8 bytes.</summary>
    public static List<string> Extract(byte[]? content)
    {
        if (content is null || content.Length == 0) return [];

        try
        {
            return Extract(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content));
        }
        catch (DecoderFallbackException)
        {
            return [];
        }
    }

    /// <summary>
    /// Markdig reports both <c>&lt;https://x&gt;</c> and a bare <c>https://x</c> as autolinks, so
    /// the span has to be checked against the source to tell them apart.
    /// </summary>
    private static bool WasAngleBracketed(string content, LinkInline link)
    {
        var start = link.Span.Start;
        return start > 0 && content[start - 1] == '<';
    }

    /// <summary>Normalizes and filters.</summary>
    private static bool TryNormalize(string? url, out string normalized)
    {
        normalized = "";

        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        // Credentials in the authority are never legitimate here and would be sent to the origin
        // by the fetcher, so a message could be used to leak a token to a host of its choosing.
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        var builder = new UriBuilder(uri) { Fragment = "" };
        normalized = builder.Uri.AbsoluteUri;
        return true;
    }
}
