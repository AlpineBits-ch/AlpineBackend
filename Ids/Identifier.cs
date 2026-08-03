namespace Ids;

/// <summary>
/// The one place an identifier is minted.
///
/// <para><c>prefix_BBBBBBBBBBBBBBBBBBBBBBBBBB</c>: a lowercase type tag, an underscore, then a
/// 26-character ULID body - a 48-bit Unix millisecond timestamp plus 80 bits of randomness in
/// Crockford base32. The body is uppercase by construction (the alphabet has no lowercase letters)
/// and sorts lexicographically by mint time under ordinal comparison, which is what a Postgres index
/// and a Scylla clustering key perform. Ordering is exact to the millisecond; ids minted within one
/// millisecond are mutually unordered.</para>
///
/// <para><b>Ids minted before this do not sort, and cannot be compared against new ones at all.</b>
/// They were upper-cased base62, which folded <c>a-z</c> onto <c>A-Z</c> and destroyed the ordering,
/// and the two encodings produce different leading characters for the same instant. There is no
/// migration - ids are foreign keys across every service, Scylla clustering keys, held by clients and
/// shared over federation. So never compare two ids to decide which is newer unless both were minted
/// after this change. Compare timestamps; see docs/specs/inbox.md.</para>
///
/// <para>Named <c>Identifier</c> and not <c>Id</c> because almost every entity has an <c>Id</c>
/// property, which would shadow the type inside its own class body.</para>
/// </summary>
public static class Identifier
{
    /// <summary>Characters in the body, excluding the prefix and separator.</summary>
    public const int BodyLength = 26;

    public const char Separator = '_';

    /// <summary>Mints an id tagged with <paramref name="prefix"/>. The separator is appended if the
    /// caller did not include one, so <c>"mesg"</c> and <c>"mesg_"</c> both yield
    /// <c>mesg_...</c>.</summary>
    /// <exception cref="ArgumentException">The prefix is empty, or is nothing but a separator. An
    /// untagged id is almost always a mistake; <see cref="NewUnprefixed"/> is the way to ask for one
    /// on purpose.</exception>
    public static string New(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("An id prefix is required. Use NewUnprefixed() for a bare id.", nameof(prefix));

        var tagLength = prefix.EndsWith(Separator) ? prefix.Length - 1 : prefix.Length;

        if (tagLength == 0)
            throw new ArgumentException("An id prefix cannot be only a separator.", nameof(prefix));

        return string.Create(tagLength + 1 + BodyLength, (prefix, tagLength), static (destination, state) =>
        {
            var (tag, length) = state;

            tag.AsSpan(0, length).CopyTo(destination);
            destination[length] = Separator;
            WriteBody(destination[(length + 1)..]);
        });
    }

    /// <summary>A bare body with no type tag, for the rare caller that wants one deliberately.</summary>
    public static string NewUnprefixed() =>
        string.Create(BodyLength, 0, static (destination, _) => WriteBody(destination));

    private static void WriteBody(Span<char> destination)
    {
        if (!Ulid.NewUlid().TryWriteStringify(destination))
        {
            // Unreachable: destination is always exactly BodyLength. Thrown rather than ignored
            // because the alternative is writing an uninitialised primary key.
            throw new InvalidOperationException($"Failed to render a {BodyLength}-character id body.");
        }
    }
}
