using System.Text.RegularExpressions;
using Alba;

namespace Identity.Tests.Helpers;

/// <summary>
/// Everything an anonymous caller can read off a response and use to tell one account apart from
/// another: the status code, the body, and the headers a handler can vary without meaning to.
/// </summary>
internal static class ResponseFingerprint
{
    /// <summary>Headers a handler realistically varies.</summary>
    private static readonly string[] ObservableHeaders =
        ["Content-Type", "Location", "WWW-Authenticate", "Retry-After", "Cache-Control"];

    /// <summary>The W3C trace id ASP.NET Core stamps into every ProblemDetails body.</summary>
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
