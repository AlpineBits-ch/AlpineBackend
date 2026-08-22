using Social.Api.Dtos.Response;

namespace Social.Api.Services;

/// <summary>How one reader stands to the canvas owner.</summary>
/// <param name="IsOwner">The reader is the owner, and sees everything.</param>
/// <param name="IsFriend">Accepted friendship in Social's own graph.</param>
/// <param name="IsMutual">Shares a guild with the owner, or has a friend in common with them.</param>
public readonly record struct CanvasViewer(bool IsOwner, bool IsFriend, bool IsMutual)
{
    public static readonly CanvasViewer Owner = new(true, true, true);

    public static readonly CanvasViewer Stranger = new(false, false, false);
}

/// <summary>
/// The per-widget gate. Applied on the server on every read and on every realtime copy, because
/// the client's preview modes are a convenience and not a boundary.
/// </summary>
public static class CanvasVisibility
{
    public static bool CanSee(string? visibility, CanvasViewer viewer)
    {
        if (viewer.IsOwner) return true;

        return visibility switch
        {
            ProfileCanvasValidator.VisibilityEveryone => true,
            ProfileCanvasValidator.VisibilityFriends => viewer.IsFriend,
            ProfileCanvasValidator.VisibilityMutuals => viewer.IsMutual,
            // A value a newer client wrote and this build does not know: fail closed.
            _ => false,
        };
    }

    /// <summary>
    /// Drops the widgets <paramref name="viewer"/> is not entitled to. Coordinates are left alone,
    /// holes included; the client re-packs on render.
    /// </summary>
    public static ProfileCanvasDto Strip(ProfileCanvasDto canvas, CanvasViewer viewer)
    {
        if (viewer.IsOwner) return canvas;

        return new ProfileCanvasDto
        {
            ProfileId = canvas.ProfileId,
            UpdatedAt = canvas.UpdatedAt,
            Version = canvas.Version,
            Theme = canvas.Theme,
            Widgets = canvas.Widgets.Where(w => CanSee(w.Visibility, viewer)).ToList(),
        };
    }

    /// <summary>True when any widget's gate needs the mutual lookup, which costs a bus hop.</summary>
    public static bool NeedsMutualLookup(IReadOnlyList<CanvasWidgetDto> widgets) =>
        widgets.Any(w => w.Visibility == ProfileCanvasValidator.VisibilityMutuals);
}
