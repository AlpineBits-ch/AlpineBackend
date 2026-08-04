using System.Security.Cryptography;

namespace Messaging.Domain.Previews;

/// <summary>
/// Identity of a message body, used as the optimistic-concurrency token for writes computed from a
/// stale read.
/// </summary>
public static class ContentHash
{
    public static string Of(byte[]? content) =>
        Convert.ToHexStringLower(SHA256.HashData(content ?? []));
}
