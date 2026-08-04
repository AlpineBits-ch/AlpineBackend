using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Bots.Contracts.Gateway.Payloads;

namespace Messaging.Domain.Previews;

/// <summary>
/// Finds the links in a message that should get a preview.
///
/// <para><b>Markdown-aware, not a regex.</b> Both exclusions that matter are syntactic: a URL
/// inside a code span or fenced block is being shown as text, not linked to - unfurling it is wrong
/// in the obvious way for anyone pasting a snippet - and <c>&lt;https://…&gt;</c> is the
/// sender-side opt-out every chat client inherited from Markdown's autolink syntax. A regex over
/// raw text cannot see either, and the parser is already a dependency of this project.</para>
///
/// <para>Pure and I/O-free on purpose: the decision about what a message links to is worth testing
/// exhaustively, and doing that against a function with no network is the difference between a
/// dozen fast cases and a fixture harness.</para>
/// </summary>
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
        // Short-circuit before parsing. This runs on every message posted on the instance, and the
        // overwhelming majority contain no link at all - a substring check is orders of magnitude
        // cheaper than building a Markdown AST for them.
        if (string.IsNullOrWhiteSpace(content)) return [];
        if (!content.Contains("://", StringComparison.Ordinal)) return [];

        var document = Markdown.Parse(content, Pipeline);
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Code exclusion is structural rather than a check in this loop, and worth being explicit
        // about because it looks like an omission: Markdig does not run inline parsing inside
        // fenced or indented code blocks, and a code span is a leaf that cannot contain a link. So
        // a URL in either position never becomes a LinkInline and is never enumerated here.
        // LinkExtractorTests pins both cases, so a future pipeline change that broke the assumption
        // would fail a test rather than start unfurling people's code snippets.
        foreach (var link in document.Descendants<LinkInline>())
        {
            // Markdig marks <https://…> as an autolink. Discord uses exactly this syntax to mean
            // "link it, but do not unfurl it", and users rely on it to post a URL without dragging
            // a card into the channel.
            if (link.IsAutoLink && WasAngleBracketed(content, link)) continue;

            if (!TryNormalize(link.Url, out var normalized)) continue;
            if (!seen.Add(normalized)) continue;

            found.Add(normalized);
            if (found.Count == EmbedLimits.MaxUnfurledPerMessage) break;
        }

        return found;
    }

    /// <summary>Convenience for the message store, whose content is UTF-8 bytes. Invalid UTF-8
    /// yields no links rather than throwing: it means the body is ciphertext or corrupt, and
    /// neither should be unfurled.</summary>
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
    /// the span has to be checked against the source to tell them apart. The character immediately
    /// before the URL text is the whole test.
    /// </summary>
    private static bool WasAngleBracketed(string content, LinkInline link)
    {
        var start = link.Span.Start;
        return start > 0 && content[start - 1] == '<';
    }

    /// <summary>
    /// Normalizes and filters. Rejects anything that is not absolute http/https - which is what
    /// keeps <c>javascript:</c>, <c>data:</c> and <c>file:</c> from ever reaching the fetcher - and
    /// strips the fragment, which never changes what a server returns and would otherwise split the
    /// cache into an entry per anchor.
    /// </summary>
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
