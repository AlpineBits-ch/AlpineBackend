using System.Security.Cryptography;

namespace Echo.Domain.Entities.Moderation;

/// <summary>The short code a person quotes back at us: <c>VNT-4KP2R9XQ</c>.</summary>
public static class PublicReference
{
    /// <summary>Crockford base32 minus the letters that get misread. 32 symbols, 5 bits each.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public const int BodyLength = 8;

    public const string Prefix = "VNT-";

    /// <summary>Total rendered length, including the prefix. Used to size the database column.</summary>
    public const int TotalLength = 4 + BodyLength;

    public static string New()
    {
        Span<char> body = stackalloc char[BodyLength];

        for (var i = 0; i < BodyLength; i++)
        {
            // One draw per character rather than slicing a single integer: GetInt32 is already
            // rejection-sampled, so there is no modulo bias to reason about here.
            body[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return string.Concat(Prefix, body);
    }

    /// <summary>
    /// Reduces whatever the user typed to the canonical form, or null if it cannot be one.
    /// </summary>
    public static string? Normalise(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        // Separators first, so what is left is only the code.
        Span<char> buffer = stackalloc char[TotalLength + 1];
        var length = 0;

        foreach (var raw in input)
        {
            if (raw is ' ' or '-' or '_') continue;
            if (length == buffer.Length) return null;   // too long to be a reference

            buffer[length++] = char.ToUpperInvariant(raw);
        }

        var code = buffer[..length];

        // The prefix is optional on input.
        if (length == BodyLength + 3 && code.StartsWith("VNT")) code = code[3..];

        if (code.Length != BodyLength) return null;

        foreach (var c in code)
        {
            if (!Alphabet.Contains(c)) return null;
        }

        return string.Concat(Prefix, code);
    }
}
