using System.Text.RegularExpressions;
using Alba;

namespace Identity.Tests.Helpers;

/// <summary>
/// Everything an anonymous caller can read off a response and use to tell one account apart from
/// another: the status code, the body, and the headers a handler can vary without meaning to.
///
/// <para>Comparing two of these for equality <i>is</i> the anti-enumeration test. Asserting only
/// "both return 202" is weaker than it looks - the oracles fixed here included a pair that agreed on
/// the status code and differed on one word of the body, and another that agreed on both and
/// differed only in which of two 400s it picked. If a future change reintroduces a difference
/// anywhere in the observable response, this fails and prints both sides.</para>
/// </summary>
internal static class ResponseFingerprint
{
    /// <summary>Headers a handler realistically varies. Not the whole header collection: Date and
    /// the response length of an identical body are either noise or already implied by it.</summary>
    private static readonly string[] ObservableHeaders =
        ["Content-Type", "Location", "WWW-Authenticate", "Retry-After", "Cache-Control"];

    /// <summary>
    /// The W3C trace id ASP.NET Core stamps into every ProblemDetails body. It is new on every
    /// request and carries nothing about the account, so leaving it in would make any two responses
    /// unequal and the comparison below worthless. This is the only thing redacted - anything else
    /// that differs is a real difference.
    /// </summary>
    private static readonly Regex TraceId = new("\"traceId\"\\s*:\\s*\"[^\"]*\"", RegexOptions.Compiled);

    public static async Task<string> OfAsync(IScenarioResult result)
    {
        var response = result.Context.Response;

        var lines = new List<string> { $"status: {response.StatusCode}" };

        lines.AddRange(ObservableHeaders.Select(header =>
            $"{header}: {(response.Headers.TryGetValue(header, out var value) ? value.ToString() : "<absent>")}"));

        lines.Add($"body: {TraceId.Replace(await result.ReadAsTextAsync(), "\"traceId\":\"<redacted>\"")}");

        return string.Join('\n', lines);
    }
}
