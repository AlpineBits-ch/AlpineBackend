using System.Security.Cryptography;

namespace Messaging.Domain.Previews;

/// <summary>
/// Identity of a message body, used as the optimistic-concurrency token for writes computed from a
/// stale read.
///
/// <para>Hashing the content rather than comparing <c>UpdatedAt</c>: the unfurl write itself moves
/// <c>UpdatedAt</c>, and so does pinning, so a timestamp comparison would reject writes that are
/// perfectly valid. What actually matters is narrower - "does this message still say what it said
/// when I extracted its links" - and that is exactly what the body hash answers.</para>
/// </summary>
public static class ContentHash
{
    public static string Of(byte[]? content) =>
        Convert.ToHexStringLower(SHA256.HashData(content ?? []));
}
