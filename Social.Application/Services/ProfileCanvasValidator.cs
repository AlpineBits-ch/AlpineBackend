using System.Text.Json;
using System.Text.RegularExpressions;
using Social.Api.Dtos.Request;
using Social.Api.Dtos.Response;

namespace Social.Api.Services;

/// <summary>
/// The limits a canvas write has to satisfy. The client enforces the same numbers, so a
/// disagreement is rejected loudly rather than truncated: a silently dropped widget looks to the
/// user like the save worked.
/// </summary>
public static partial class ProfileCanvasValidator
{
    public const int Columns = 4;
    public const int MaxWidgets = 20;
    public const int MaxSpacers = 20;
    public const int MaxCardWidgets = 2;
    public const int MaxImagesPerProfile = 8;

    /// <summary>Per-widget cap on the serialized <c>config</c>, in UTF-8 bytes.</summary>
    public const int MaxConfigBytes = 8 * 1024;

    public const int MaxWidgetIdLength = 64;
    public const int MaxWidgetTypeLength = 64;

    public const string SpacerType = "spacer";

    public const string VisibilityEveryone = "everyone";
    public const string VisibilityFriends = "friends";
    public const string VisibilityMutuals = "mutuals";

    public const string BackdropGradient = "gradient";
    public const string BackdropImage = "image";

    /// <summary>The only footprints that validate.</summary>
    private static readonly (int W, int H)[] Footprints = [(1, 1), (2, 1), (2, 2), (4, 1), (4, 2)];

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColorRegex();

    /// <summary>Null when the write is acceptable, otherwise the reason naming the field.</summary>
    public static string? Validate(CanvasWriteDto dto)
    {
        if (dto.Widgets is null) return "widgets is required.";
        if (dto.Theme is null) return "theme is required.";

        var themeProblem = ValidateTheme(dto.Theme);
        if (themeProblem is not null) return themeProblem;

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var spacers = 0;
        var widgets = 0;
        var cards = 0;

        for (var i = 0; i < dto.Widgets.Count; i++)
        {
            var widget = dto.Widgets[i];
            var problem = ValidateWidget(widget, i);
            if (problem is not null) return problem;

            if (!seenIds.Add(widget.Id)) return $"widgets[{i}].id is a duplicate.";

            if (widget.Type == SpacerType) spacers++;
            else widgets++;

            if (widget.Card) cards++;
        }

        if (widgets > MaxWidgets) return $"widgets holds more than {MaxWidgets} non-spacer widgets.";
        if (spacers > MaxSpacers) return $"widgets holds more than {MaxSpacers} spacers.";
        if (cards > MaxCardWidgets) return $"widgets holds more than {MaxCardWidgets} widgets with card set.";

        return null;
    }

    private static string? ValidateWidget(CanvasWidgetDto widget, int index)
    {
        if (string.IsNullOrWhiteSpace(widget.Id)) return $"widgets[{index}].id is required.";
        if (widget.Id.Length > MaxWidgetIdLength)
            return $"widgets[{index}].id exceeds {MaxWidgetIdLength} characters.";

        if (string.IsNullOrWhiteSpace(widget.Type)) return $"widgets[{index}].type is required.";
        if (widget.Type.Length > MaxWidgetTypeLength)
            return $"widgets[{index}].type exceeds {MaxWidgetTypeLength} characters.";

        if (!IsGridValue(widget.X)) return $"widgets[{index}].x must be a finite non-negative integer.";
        if (!IsGridValue(widget.Y)) return $"widgets[{index}].y must be a finite non-negative integer.";
        if (!IsGridValue(widget.W)) return $"widgets[{index}].w must be a finite non-negative integer.";
        if (!IsGridValue(widget.H)) return $"widgets[{index}].h must be a finite non-negative integer.";

        if (!Footprints.Any(f => f.W == widget.W && f.H == widget.H))
        {
            var allowed = string.Join(", ", Footprints.Select(f => $"{f.W}x{f.H}"));
            return $"widgets[{index}] has footprint {(int)widget.W}x{(int)widget.H}; allowed: {allowed}.";
        }

        if (widget.X + widget.W > Columns)
            return $"widgets[{index}] runs past column {Columns}: x + w must not exceed {Columns}.";

        if (!IsKnownVisibility(widget.Visibility))
            return $"widgets[{index}].visibility must be one of: {VisibilityEveryone}, {VisibilityFriends}, {VisibilityMutuals}.";

        var configBytes = JsonSerializer.SerializeToUtf8Bytes(widget.Config, CanvasJson.Options).Length;
        if (configBytes > MaxConfigBytes)
            return $"widgets[{index}].config exceeds {MaxConfigBytes} bytes.";

        return null;
    }

    private static string? ValidateTheme(CanvasThemeDto theme)
    {
        if (theme.Accent is not null && !HexColorRegex().IsMatch(theme.Accent))
            return "theme.accent must be a hex colour like #5865F2, or null.";

        if (theme.Backdrop is null) return null;

        if (theme.Backdrop.Kind is not (BackdropGradient or BackdropImage))
            return $"theme.backdrop.kind must be one of: {BackdropGradient}, {BackdropImage}.";

        if (theme.Backdrop.Kind == BackdropGradient)
        {
            if (theme.Backdrop.From is not null && !HexColorRegex().IsMatch(theme.Backdrop.From))
                return "theme.backdrop.from must be a hex colour like #5865F2.";
            if (theme.Backdrop.To is not null && !HexColorRegex().IsMatch(theme.Backdrop.To))
                return "theme.backdrop.to must be a hex colour like #5865F2.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(theme.Backdrop.ImageId))
            return "theme.backdrop.imageId is required when kind is image.";

        return null;
    }

    public static bool IsKnownVisibility(string? visibility) =>
        visibility is VisibilityEveryone or VisibilityFriends or VisibilityMutuals;

    // JSON has no NaN or Infinity literal, but 1e999 parses to one and the client's own layout
    // engine once hung on h: Infinity, so the check is explicit rather than left to the binder.
    private static bool IsGridValue(double value) =>
        double.IsFinite(value) && value >= 0 && Math.Floor(value) == value;
}
