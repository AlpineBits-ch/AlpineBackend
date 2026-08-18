using System.Globalization;
using System.Text.Json;
using Guild.Application.Dtos.Response;
using Guild.Domain.Services;

namespace Guild.Application.Services;

/// <summary>
/// Turns an evaluated roll into the two halves a message carries: plain text any unmodified client
/// can read, and the structured roll for one that renders it richly. Both come from the same
/// outcome, so a client cannot be shown a total the text disagrees with.
/// </summary>
public static class DiceRollRenderer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Long enough for a sentence, short enough not to be the message.</summary>
    public const int MaxReasonLength = 120;

    /// <summary>Builds the message body.</summary>
    /// <param name="outcome">The evaluated roll.</param>
    /// <param name="reason">What the roller said they were rolling for, or null.</param>
    /// <returns>A single line such as <c>Perception: 4d6kh3 (6, 5, 3, ~1) + 2 = 16</c>.</returns>
    public static string Content(DiceRollOutcome outcome, string? reason)
    {
        var line = $"{outcome.Breakdown} = {outcome.Total.ToString(CultureInfo.InvariantCulture)}";
        return string.IsNullOrWhiteSpace(reason) ? line : $"{reason}: {line}";
    }

    /// <summary>Builds the structured half, shared by the embed and by the stored record.</summary>
    /// <param name="outcome">The evaluated roll.</param>
    /// <returns>The roll in the shape clients and the <c>DiceRoll</c> row both use.</returns>
    public static DiceRollResultsJson Structured(DiceRollOutcome outcome) => new()
    {
        Expression = outcome.Expression,
        Total = outcome.Total,
        Breakdown = outcome.Breakdown,
        Terms = outcome.Terms.Select(DiceRollTermDto.From).ToList(),
    };

    /// <summary>The structured half, serialized for the <c>DiceRoll</c> row.</summary>
    /// <param name="outcome">The evaluated roll.</param>
    /// <returns>JSON.</returns>
    public static string ResultsJson(DiceRollOutcome outcome) => JsonSerializer.Serialize(Structured(outcome), Json);

    /// <summary>
    /// The message's <c>EmbedsJson</c>: a Discord-shaped embed so an existing renderer shows the
    /// breakdown, carrying the roll itself on an extra member that such a renderer ignores.
    /// </summary>
    /// <param name="outcome">The evaluated roll.</param>
    /// <param name="reason">What the roller said they were rolling for, or null.</param>
    /// <returns>A one-element JSON array.</returns>
    public static string EmbedsJson(DiceRollOutcome outcome, string? reason)
    {
        var fields = new List<object>
        {
            new { name = "Expression", value = outcome.Expression, inline = true },
            new { name = "Total", value = outcome.Total.ToString(CultureInfo.InvariantCulture), inline = true },
        };

        return JsonSerializer.Serialize(new[]
        {
            new
            {
                type = "rich",
                title = string.IsNullOrWhiteSpace(reason) ? outcome.Expression : reason,
                description = outcome.Breakdown,
                fields,
                dice = Structured(outcome),
            },
        }, Json);
    }
}
