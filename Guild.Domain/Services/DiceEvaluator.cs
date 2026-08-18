using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Guild.Domain.Services;

/// <summary>Where a die's face comes from.</summary>
public interface IDieRoller
{
    /// <summary>Rolls one die.</summary>
    /// <param name="sides">The die size, at least two.</param>
    /// <returns>A face between 1 and <paramref name="sides"/> inclusive.</returns>
    int Roll(int sides);
}

/// <summary>
/// The only roller the endpoint uses. The whole point of a server-rolled die is that the result is
/// not the roller's to influence, and <c>Random</c> is seedable and predictable from observed output.
/// </summary>
public sealed class SecureDieRoller : IDieRoller
{
    /// <inheritdoc />
    public int Roll(int sides) => RandomNumberGenerator.GetInt32(1, sides + 1);
}

/// <summary>What one term of an expression produced.</summary>
public sealed record DiceTermResult
{
    /// <summary>The term as written back to the roller, for example <c>4d6kh3</c>.</summary>
    public required string Notation { get; init; }

    /// <summary>1 or -1.</summary>
    public required int Sign { get; init; }

    /// <summary>Set for a constant term, null for a dice pool.</summary>
    public int? Constant { get; init; }

    /// <summary>Every physical roll in order, explosions included.</summary>
    public IReadOnlyList<int> Rolls { get; init; } = [];

    /// <summary>One entry per die, each already carrying whatever it exploded into.</summary>
    public IReadOnlyList<int> Dice { get; init; } = [];

    /// <summary>The subset of <see cref="Dice"/> that a keep or drop mode let count.</summary>
    public IReadOnlyList<int> Kept { get; init; } = [];

    /// <summary>The term's contribution before its sign is applied.</summary>
    public required long Subtotal { get; init; }
}

/// <summary>One evaluated expression.</summary>
public sealed record DiceRollOutcome(string Expression, IReadOnlyList<DiceTermResult> Terms, long Total, string Breakdown);

/// <summary>
/// Rolls a parsed expression. Every bound is already enforced by
/// <see cref="DiceNotationParser"/>, so this walks a pool it knows to be small.
/// </summary>
public static class DiceEvaluator
{
    /// <summary>Beyond this the breakdown stops being readable and starts being a wall of numbers.</summary>
    private const int MaxRenderedDice = 40;

    /// <summary>
    /// Evaluates a parsed expression.
    /// </summary>
    /// <param name="terms">The parsed terms.</param>
    /// <param name="expression">The normalized expression, echoed back on the outcome.</param>
    /// <param name="roller">Where faces come from.</param>
    /// <returns>The per-term detail, the total and a plain-text breakdown.</returns>
    public static DiceRollOutcome Evaluate(IReadOnlyList<DiceTerm> terms, string expression, IDieRoller roller)
    {
        var results = new List<DiceTermResult>(terms.Count);
        long total = 0;

        foreach (var term in terms)
        {
            var result = term.Constant is { } constant
                ? new DiceTermResult { Notation = term.Notation, Sign = term.Sign, Constant = constant, Subtotal = constant }
                : EvaluateDice(term, roller);

            results.Add(result);
            total += term.Sign * result.Subtotal;
        }

        return new DiceRollOutcome(expression, results, total, Render(results));
    }

    private static DiceTermResult EvaluateDice(DiceTerm term, IDieRoller roller)
    {
        var rolls = new List<int>(term.Count);
        var dice = new List<int>(term.Count);

        for (var i = 0; i < term.Count; i++)
        {
            var face = roller.Roll(term.Sides);
            rolls.Add(face);
            var value = face;

            // Explosions resolve into the die that caused them, so a keep or drop mode compares
            // whole dice rather than treating an explosion as an extra die to discard.
            var chain = 0;
            while (term.Explodes && face == term.Sides && chain < DiceLimits.MaxExplosionsPerDie)
            {
                face = roller.Roll(term.Sides);
                rolls.Add(face);
                value += face;
                chain++;
            }

            dice.Add(value);
        }

        var kept = SelectKept(term, dice);
        return new DiceTermResult
        {
            Notation = term.Notation,
            Sign = term.Sign,
            Rolls = rolls,
            Dice = dice,
            Kept = kept,
            Subtotal = kept.Sum(d => (long)d),
        };
    }

    private static List<int> SelectKept(DiceTerm term, List<int> dice)
    {
        if (term.Keep == DiceKeepMode.All) return [..dice];

        var ordered = dice
            .Select((value, position) => (value, position))
            .OrderByDescending(d => d.value)
            .ThenBy(d => d.position)
            .ToList();

        var take = term.Keep switch
        {
            DiceKeepMode.KeepHighest => ordered.Take(term.KeepCount),
            DiceKeepMode.KeepLowest => ordered.TakeLast(term.KeepCount),
            DiceKeepMode.DropHighest => ordered.Skip(term.KeepCount),
            _ => ordered.SkipLast(term.KeepCount),
        };

        // Back into roll order, so the breakdown reads left to right the way the dice were thrown.
        return take.OrderBy(d => d.position).Select(d => d.value).ToList();
    }

    private static string Render(IReadOnlyList<DiceTermResult> results)
    {
        var builder = new StringBuilder();

        foreach (var result in results)
        {
            if (builder.Length > 0) builder.Append(result.Sign < 0 ? " - " : " + ");
            else if (result.Sign < 0) builder.Append('-');

            if (result.Constant is { } constant)
            {
                builder.Append(constant.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            builder.Append(result.Notation).Append(" (").Append(RenderDice(result)).Append(')');
        }

        return builder.ToString();
    }

    private static string RenderDice(DiceTermResult result)
    {
        var remaining = new List<int>(result.Kept);
        var parts = new List<string>(result.Dice.Count);

        foreach (var die in result.Dice.Take(MaxRenderedDice))
        {
            // A dropped die is shown struck through with a tilde rather than hidden, because
            // seeing what was discarded is most of why anybody rolls keep-highest.
            var counted = remaining.Remove(die);
            parts.Add(counted ? die.ToString(CultureInfo.InvariantCulture) : $"~{die}");
        }

        if (result.Dice.Count > MaxRenderedDice) parts.Add("...");

        return string.Join(", ", parts);
    }
}
