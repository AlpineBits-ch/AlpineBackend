using System.Globalization;
using System.Text;

namespace Guild.Domain.Services;

/// <summary>
/// Everything the dice parser refuses before it evaluates anything. The expression arrives as
/// untrusted text, and <c>999999d999999</c> is a denial of service that needs no parser bug.
/// </summary>
public static class DiceLimits
{
    /// <summary>Notation, not prose - the longest sensible expression is well under this.</summary>
    public const int MaxExpressionLength = 128;

    public const int MaxTerms = 16;
    public const int MaxDicePerTerm = 100;

    /// <summary>Across the whole expression, so sixteen legal terms cannot add up to an illegal pool.</summary>
    public const int MaxDiceTotal = 100;

    /// <summary>A d1 always shows its maximum face, so an exploding one never terminates. Refusing
    /// the die size removes the case instead of special-casing the explosion.</summary>
    public const int MinSides = 2;

    public const int MaxSides = 1000;
    public const int MaxConstant = 1_000_000;

    /// <summary>How far one exploding die may chain. At the worst die size this is a 2^-10 tail.</summary>
    public const int MaxExplosionsPerDie = 10;

    /// <summary>Longest run of digits accepted anywhere, which keeps parsing clear of overflow.</summary>
    private const int MaxDigits = 7;

    internal static bool TryReadNumber(string text, ref int index, out int value)
    {
        value = 0;
        var start = index;
        while (index < text.Length && char.IsAsciiDigit(text[index])) index++;

        var length = index - start;
        if (length == 0 || length > MaxDigits) return false;

        return int.TryParse(text.AsSpan(start, length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>Which subset of a dice pool counts toward the total.</summary>
public enum DiceKeepMode
{
    /// <summary>Every die counts.</summary>
    All,

    KeepHighest,
    KeepLowest,
    DropHighest,
    DropLowest,
}

/// <summary>
/// One addend of an expression: either a constant or a pool of dice. <see cref="Sign"/> is the
/// arithmetic between terms, so <c>3d6+2d4-1</c> is three terms rather than a tree.
/// </summary>
public sealed record DiceTerm
{
    /// <summary>1 or -1.</summary>
    public required int Sign { get; init; }

    /// <summary>Set for a literal, null for a dice pool.</summary>
    public int? Constant { get; init; }

    public int Count { get; init; }
    public int Sides { get; init; }
    public DiceKeepMode Keep { get; init; } = DiceKeepMode.All;

    /// <summary>How many dice the keep or drop mode applies to, ignored under <see cref="DiceKeepMode.All"/>.</summary>
    public int KeepCount { get; init; }

    /// <summary>Whether a die showing its maximum face is rolled again and added.</summary>
    public bool Explodes { get; init; }

    /// <summary>The term as it is rendered back to the roller.</summary>
    public string Notation { get; init; } = string.Empty;
}

/// <summary>A parsed expression, or the reason it was refused.</summary>
public sealed record DiceParseResult(bool Ok, IReadOnlyList<DiceTerm> Terms, string Normalized, string? Error)
{
    internal static DiceParseResult Fail(string error) => new(false, [], string.Empty, error);
}

/// <summary>
/// The system-neutral dice grammar: <c>NdX</c> with arithmetic between terms, keep and drop,
/// advantage and disadvantage, and exploding dice. Pure and total - it never throws and never
/// rolls anything, so the bounds are all enforced before a single die is evaluated.
/// </summary>
public static class DiceNotationParser
{
    /// <summary>
    /// Parses a dice expression.
    /// </summary>
    /// <param name="expression">Untrusted notation such as <c>4d6kh3+2</c>.</param>
    /// <returns>The parsed terms, or a one-sentence refusal.</returns>
    public static DiceParseResult Parse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return DiceParseResult.Fail("An expression is required.");

        // Measured before anything copies it, so an oversize body is refused rather than processed.
        if (expression.Length > DiceLimits.MaxExpressionLength)
            return DiceParseResult.Fail($"An expression cannot exceed {DiceLimits.MaxExpressionLength} characters.");

        // Whitespace is stripped up front so "3d6 + 2" and "3d6+2" cannot parse differently.
        var text = Compact(expression);
        if (text.Length == 0) return DiceParseResult.Fail("An expression is required.");

        var terms = new List<DiceTerm>();
        var index = 0;
        var diceTotal = 0;
        var sign = 1;

        if (text[0] is '+' or '-')
        {
            sign = text[0] == '-' ? -1 : 1;
            index++;
        }

        while (true)
        {
            if (terms.Count >= DiceLimits.MaxTerms)
                return DiceParseResult.Fail($"An expression cannot have more than {DiceLimits.MaxTerms} terms.");

            if (ParseTerm(text, ref index, sign) is not { } parsed) return DiceParseResult.Fail(Describe(text, index));
            if (parsed.Error is not null) return DiceParseResult.Fail(parsed.Error);

            var term = parsed.Term!;
            diceTotal += term.Count;
            if (diceTotal > DiceLimits.MaxDiceTotal)
                return DiceParseResult.Fail($"An expression cannot roll more than {DiceLimits.MaxDiceTotal} dice.");

            terms.Add(term);

            if (index >= text.Length) break;
            if (text[index] is not ('+' or '-')) return DiceParseResult.Fail(Describe(text, index));

            sign = text[index] == '-' ? -1 : 1;
            index++;
        }

        return new DiceParseResult(true, terms, Render(terms), null);
    }

    private static string Compact(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character)) builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private readonly record struct TermParse(DiceTerm? Term, string? Error);

    private static TermParse? ParseTerm(string text, ref int index, int sign)
    {
        if (index >= text.Length) return null;

        // The count is optional so that "d20" reads as one die, which is what everybody types.
        var hasCount = char.IsAsciiDigit(text[index]);
        var count = 1;
        if (hasCount && !DiceLimits.TryReadNumber(text, ref index, out count)) return null;

        if (index >= text.Length || text[index] != 'd')
        {
            if (!hasCount) return null;
            if (count > DiceLimits.MaxConstant)
                return new TermParse(null, $"A constant cannot exceed {DiceLimits.MaxConstant}.");

            return new TermParse(new DiceTerm
            {
                Sign = sign,
                Constant = count,
                Notation = count.ToString(CultureInfo.InvariantCulture),
            }, null);
        }

        index++;
        if (!DiceLimits.TryReadNumber(text, ref index, out var sides)) return null;

        if (count < 1 || count > DiceLimits.MaxDicePerTerm)
            return new TermParse(null, $"A term cannot roll more than {DiceLimits.MaxDicePerTerm} dice.");

        if (sides < DiceLimits.MinSides || sides > DiceLimits.MaxSides)
            return new TermParse(null, $"A die must have between {DiceLimits.MinSides} and {DiceLimits.MaxSides} sides.");

        var keep = DiceKeepMode.All;
        var keepCount = 0;
        var explodes = false;

        while (index < text.Length)
        {
            if (text[index] == '!')
            {
                if (explodes) return new TermParse(null, "A term can only explode once.");
                explodes = true;
                index++;
                continue;
            }

            var modifier = ReadKeepModifier(text, ref index, count);
            if (modifier is null) break;
            if (modifier.Value.Error is not null) return new TermParse(null, modifier.Value.Error);

            if (keep != DiceKeepMode.All)
                return new TermParse(null, "A term can only keep or drop one way.");

            keep = modifier.Value.Mode;
            keepCount = modifier.Value.Count;

            // Advantage on a single die is the whole point of the shorthand, so it rolls a second
            // one rather than keeping the highest of a pool of one and changing nothing.
            if (modifier.Value.Shorthand) count = Math.Max(count, 2);
        }

        var term = new DiceTerm
        {
            Sign = sign,
            Count = count,
            Sides = sides,
            Keep = keep,
            KeepCount = keepCount,
            Explodes = explodes,
        };

        return new TermParse(term with { Notation = RenderTerm(term) }, null);
    }

    private readonly record struct KeepParse(DiceKeepMode Mode, int Count, string? Error, bool Shorthand = false);

    /// <summary>
    /// Reads <c>kh</c>, <c>kl</c>, <c>dh</c>, <c>dl</c>, <c>adv</c> or <c>dis</c>, returning null
    /// when the next characters are not a modifier at all.
    /// </summary>
    private static KeepParse? ReadKeepModifier(string text, ref int index, int diceCount)
    {
        if (index >= text.Length) return null;

        if (text[index] == 'a')
        {
            if (!Matches(text, index, "adv")) return null;
            index += 3;
            return new KeepParse(DiceKeepMode.KeepHighest, 1, null, Shorthand: true);
        }

        if (text[index] == 'd' && Matches(text, index, "dis"))
        {
            index += 3;
            return new KeepParse(DiceKeepMode.KeepLowest, 1, null, Shorthand: true);
        }

        if (index + 1 >= text.Length) return null;
        if (text[index] is not ('k' or 'd')) return null;

        var mode = (text[index], text[index + 1]) switch
        {
            ('k', 'h') => DiceKeepMode.KeepHighest,
            ('k', 'l') => DiceKeepMode.KeepLowest,
            ('d', 'h') => DiceKeepMode.DropHighest,
            ('d', 'l') => DiceKeepMode.DropLowest,
            _ => DiceKeepMode.All,
        };

        if (mode == DiceKeepMode.All) return null;

        var cursor = index + 2;
        var amount = 1;
        if (cursor < text.Length && char.IsAsciiDigit(text[cursor]) &&
            !DiceLimits.TryReadNumber(text, ref cursor, out amount))
        {
            return new KeepParse(DiceKeepMode.All, 0, "That keep or drop count is not a number.");
        }

        index = cursor;

        if (amount < 1 || amount >= diceCount + (mode is DiceKeepMode.KeepHighest or DiceKeepMode.KeepLowest ? 1 : 0))
        {
            return new KeepParse(DiceKeepMode.All, 0,
                "A keep or drop count has to leave at least one die and cannot exceed the pool.");
        }

        return new KeepParse(mode, amount, null);
    }

    private static bool Matches(string text, int index, string word) =>
        index + word.Length <= text.Length && text.AsSpan(index, word.Length).SequenceEqual(word);

    private static string Describe(string text, int index) =>
        index < text.Length
            ? $"That expression is not valid dice notation at '{text[index]}'."
            : "That expression is not valid dice notation.";

    private static string Render(IReadOnlyList<DiceTerm> terms)
    {
        var builder = new StringBuilder();
        foreach (var term in terms)
        {
            if (builder.Length > 0) builder.Append(term.Sign < 0 ? " - " : " + ");
            else if (term.Sign < 0) builder.Append('-');

            builder.Append(term.Notation);
        }

        return builder.ToString();
    }

    private static string RenderTerm(DiceTerm term)
    {
        if (term.Constant is { } constant) return constant.ToString(CultureInfo.InvariantCulture);

        var builder = new StringBuilder();
        builder.Append(term.Count).Append('d').Append(term.Sides);

        builder.Append(term.Keep switch
        {
            DiceKeepMode.KeepHighest => $"kh{term.KeepCount}",
            DiceKeepMode.KeepLowest => $"kl{term.KeepCount}",
            DiceKeepMode.DropHighest => $"dh{term.KeepCount}",
            DiceKeepMode.DropLowest => $"dl{term.KeepCount}",
            _ => string.Empty,
        });

        if (term.Explodes) builder.Append('!');

        return builder.ToString();
    }
}
