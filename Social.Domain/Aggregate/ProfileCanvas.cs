using Persistence;

namespace Social.Domain.Aggregate;

/// <summary>
/// One profile's arranged widget grid. Theme and widgets are stored as the JSON the API speaks,
/// because widget <c>config</c> is owned by the widget type and the server never models it.
/// </summary>
public class ProfileCanvas : BaseEntity<ProfileCanvas>, IPrefixedEntity
{
    public static string Prefix { get; } = "cnvs";

    public string ProfileId { get; set; } = null!;

    public virtual Profile Profile { get; set; } = null!;

    /// <summary>Starts at 1 and increments on every accepted write.</summary>
    public int Version { get; set; } = 1;

    public string ThemeJson { get; set; } = null!;

    public string WidgetsJson { get; set; } = null!;

    public static ProfileCanvas Create(string profileId, string themeJson, string widgetsJson) => new()
    {
        Id = GenerateId(),
        ProfileId = profileId,
        Version = 1,
        ThemeJson = themeJson,
        WidgetsJson = widgetsJson,
    };
}

/// <summary>An image uploaded for use inside a canvas, counted against a per-profile cap.</summary>
public class ProfileCanvasImage : BaseEntity<ProfileCanvasImage>, IPrefixedEntity
{
    public static string Prefix { get; } = "cnvi";

    public string ProfileId { get; set; } = null!;

    public virtual Profile Profile { get; set; } = null!;

    public string ContentType { get; set; } = null!;

    public long SizeBytes { get; set; }

    public static ProfileCanvasImage Create(string profileId, string contentType, long sizeBytes) => new()
    {
        Id = GenerateId(),
        ProfileId = profileId,
        ContentType = contentType,
        SizeBytes = sizeBytes,
    };
}
