using System.Text.Json;
using System.Text.Json.Serialization;

namespace Social.Api.Dtos.Response;

/// <summary>The canvas background: either a two-stop gradient or one uploaded image.</summary>
public class CanvasBackdropDto
{
    /// <summary><c>gradient</c> or <c>image</c>.</summary>
    public string Kind { get; set; } = null!;

    /// <summary>Gradient stop, ignored when <see cref="Kind"/> is <c>image</c>.</summary>
    public string? From { get; set; }

    /// <summary>Gradient stop, ignored when <see cref="Kind"/> is <c>image</c>.</summary>
    public string? To { get; set; }

    /// <summary>Canvas image id, ignored when <see cref="Kind"/> is <c>gradient</c>.</summary>
    public string? ImageId { get; set; }
}

/// <summary>Canvas-wide styling.</summary>
public class CanvasThemeDto
{
    /// <summary>Hex accent, or null to fall back to the profile's accent colour.</summary>
    public string? Accent { get; set; }

    public CanvasBackdropDto? Backdrop { get; set; }
}

/// <summary>One placed widget.</summary>
public class CanvasWidgetDto
{
    public string Id { get; set; } = null!;

    /// <summary>Free-form: the client ships new widget types without a server change, and an
    /// unknown type draws nothing.</summary>
    public string Type { get; set; } = null!;

    // Doubles, not ints: a hand-rolled client can put a fraction, a NaN or an Infinity here and
    // the validator has to name the offending field rather than fail in the model binder.
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }

    /// <summary><c>everyone</c>, <c>friends</c> or <c>mutuals</c>.</summary>
    public string Visibility { get; set; } = null!;

    /// <summary>Shown in the profile popout, at most two per canvas.</summary>
    public bool Card { get; set; }

    /// <summary>Opaque to the server: stored and returned exactly as it arrived.</summary>
    public JsonElement Config { get; set; }
}

/// <summary>A profile's whole canvas, already stripped for the caller.</summary>
public class ProfileCanvasDto
{
    public string ProfileId { get; set; } = null!;

    public DateTimeOffset UpdatedAt { get; set; }

    public int Version { get; set; }

    public CanvasThemeDto Theme { get; set; } = new();

    public IReadOnlyList<CanvasWidgetDto> Widgets { get; set; } = [];
}

/// <summary>An uploaded canvas image and where to fetch it.</summary>
public class CanvasImageDto
{
    public string ImageId { get; set; } = null!;

    public string Url { get; set; } = null!;
}

/// <summary>The <c>social.ProfileCanvasUpdated</c> websocket payload.</summary>
public class ProfileCanvasUpdatedPayload
{
    public string ProfileId { get; set; } = null!;

    /// <summary>Stripped for the recipient this copy is addressed to.</summary>
    public ProfileCanvasDto Canvas { get; set; } = null!;
}

/// <summary>
/// The serializer both the wire and the jsonb columns use. Web defaults, so the stored JSON is the
/// camelCase the client already speaks and a round trip through storage changes no key.
/// </summary>
public static class CanvasJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
