namespace Ids;

/// <summary>The one place an identifier is minted.</summary>
public static class Identifier
{
    /// <summary>Characters in the body, excluding the prefix and separator.</summary>
    public const int BodyLength = 26;

    public const char Separator = '_';

    /// <summary>Mints an id tagged with <paramref name="prefix"/>.</summary>
    /// <exception cref="ArgumentException">
    /// The prefix is empty, or is nothing but a separator.
    /// </exception>
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
            // Unreachable: destination is always exactly BodyLength.
            throw new InvalidOperationException($"Failed to render a {BodyLength}-character id body.");
        }
    }
}
