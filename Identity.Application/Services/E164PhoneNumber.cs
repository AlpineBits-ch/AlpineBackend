using System.Text;

namespace Identity.Application.Services;

/// <summary>
/// Canonicalisation and redaction for the phone number an account records on itself.
/// </summary>
public static class E164PhoneNumber
{
    /// <summary>E.164 caps the digits after the <c>+</c> at fifteen.</summary>
    public const int MaxDigits = 15;

    /// <summary>The shortest numbers in service, country code included, run to about seven digits.
    /// Set below any real floor on purpose - the bound exists to reject <c>"+"</c> and <c>"+1"</c>,
    /// not to adjudicate numbering plans this class has already declined to know about.</summary>
    public const int MinDigits = 6;

    /// <summary>
    /// Returns the canonical E.164 form of <paramref name="candidate"/>, or null when it is not a
    /// number this system will store.
    /// </summary>
    public static string? Normalize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;

        var trimmed = candidate.Trim();
        if (trimmed[0] != '+') return null;

        var digits = new StringBuilder(trimmed.Length);

        foreach (var character in trimmed.AsSpan(1))
        {
            if (char.IsAsciiDigit(character))
            {
                digits.Append(character);
                continue;
            }

            // Separators people paste, and nothing else.
            if (IsSeparator(character)) continue;

            return null;
        }

        if (digits.Length is < MinDigits or > MaxDigits) return null;

        // A leading zero is a national trunk prefix that E.164 does not carry, and there is no
        // country code that starts with one - so this is the "+0..." and "00..." case arriving in
        // the shape that got past the '+' check.
        if (digits[0] == '0') return null;

        return string.Concat("+", digits.ToString());
    }

    /// <summary>Whether the character is presentation rather than part of the number.</summary>
    private static bool IsSeparator(char character) =>
        character is ' ' or '-' or '.' or '(' or ')' or '\u00A0';

    /// <summary>
    /// The form that may be written into an audit row or a log line: a short prefix, then dots,
    /// then the last two digits.
    /// </summary>
    public static string Mask(string? e164)
    {
        if (string.IsNullOrWhiteSpace(e164)) return "(none)";

        // A fixed prefix rather than a parsed country code, so this does not need a country table
        // that would then have to be kept in step with reality.
        const int prefixLength = 3;
        const int suffixLength = 2;

        return e164.Length <= prefixLength + suffixLength
            ? new string('*', e164.Length)
            : $"{e164[..prefixLength]}***{e164[^suffixLength..]}";
    }
}
