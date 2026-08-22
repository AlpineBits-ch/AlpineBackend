using Social.Api.Dtos.Response;

namespace Social.Api.Dtos.Request;

/// <summary>What a canvas PUT carries. Profile id, version and timestamp are server-owned.</summary>
public class CanvasWriteDto
{
    public CanvasThemeDto? Theme { get; set; }

    public IReadOnlyList<CanvasWidgetDto>? Widgets { get; set; }
}
