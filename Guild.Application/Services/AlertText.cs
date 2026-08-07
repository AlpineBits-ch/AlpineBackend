namespace Guild.Application.Services;

/// <summary>
/// One line of notification copy, in both forms: the English the server can always render, and the
/// key plus arguments a client renders in the reader's own language instead.
/// </summary>
public sealed class AlertText
{
    /// <summary>Always present. What a reader who cannot resolve <see cref="LocKey"/> sees.</summary>
    public required string Text { get; init; }

    /// <summary>A key from <see cref="HouseholdLocKeys"/>, or null when <see cref="Text"/> is user
    /// content rather than server copy.</summary>
    public string? LocKey { get; init; }

    /// <summary>The values the key's placeholders take, in order.</summary>
    public IReadOnlyList<string> LocArgs { get; init; } = [];

    /// <summary>User content: shown as-is in every language.</summary>
    public static AlertText Raw(string text) => new() { Text = text };

    /// <summary>Server copy: <paramref name="text"/> is the English rendering of
    /// <paramref name="key"/> with <paramref name="args"/> already substituted in.</summary>
    public static AlertText Loc(string key, string text, params string[] args) =>
        new() { Text = text, LocKey = key, LocArgs = args };

    public static implicit operator string(AlertText text) => text.Text;
}
