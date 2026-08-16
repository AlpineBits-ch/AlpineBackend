using System.Security.Cryptography;
using System.Text.Json;

namespace Social.Api.Helpers;

/// <summary>Strong validators over a response body.</summary>
public static class ETagHelper
{
    public static string Compute(object body)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body);
        return $"\"{Convert.ToHexString(SHA256.HashData(json))[..32]}\"";
    }

    /// <summary>Whether an If-None-Match header covers <paramref name="etag"/>.</summary>
    public static bool Matches(string? ifNoneMatch, string etag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch)) return false;
        if (ifNoneMatch.Trim() == "*") return true;

        foreach (var candidate in ifNoneMatch.Split(','))
        {
            var trimmed = candidate.Trim();
            // A weak validator is still a match for our purposes: the body is either identical or
            // it is not, and we only ever mint strong ones.
            if (trimmed.StartsWith("W/", StringComparison.Ordinal)) trimmed = trimmed[2..];
            if (trimmed == etag) return true;
        }
        return false;
    }
}
