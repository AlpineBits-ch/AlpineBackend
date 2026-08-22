using System.Text.Json;
using Social.Api.Dtos.Request;
using Social.Api.Dtos.Response;
using Social.Api.Services;

namespace Social.Tests.Services;

/// <summary>The limits a hand-rolled request has to fail on, since the client enforces its own.</summary>
[TestFixture]
public class ProfileCanvasValidatorTests
{
    private static JsonElement Config(string json = "{}") => JsonDocument.Parse(json).RootElement;

    private static CanvasWidgetDto Widget(
        string id = "w1",
        string type = "quote",
        double x = 0, double y = 0, double w = 1, double h = 1,
        string visibility = "everyone",
        bool card = false,
        string config = "{}") => new()
    {
        Id = id, Type = type, X = x, Y = y, W = w, H = h,
        Visibility = visibility, Card = card, Config = Config(config),
    };

    private static CanvasWriteDto Write(params CanvasWidgetDto[] widgets) => new()
    {
        Theme = new CanvasThemeDto(),
        Widgets = widgets,
    };

    [Test]
    public void A_plain_canvas_validates()
    {
        Assert.That(ProfileCanvasValidator.Validate(Write(Widget())), Is.Null);
    }

    [Test]
    public void Widgets_is_required()
    {
        Assert.That(ProfileCanvasValidator.Validate(new CanvasWriteDto { Theme = new CanvasThemeDto() }),
            Does.Contain("widgets"));
    }

    [Test]
    public void Theme_is_required()
    {
        Assert.That(ProfileCanvasValidator.Validate(new CanvasWriteDto { Widgets = [] }), Does.Contain("theme"));
    }

    [Test]
    public void More_than_twenty_non_spacer_widgets_is_rejected()
    {
        var widgets = Enumerable.Range(0, ProfileCanvasValidator.MaxWidgets + 1)
            .Select(i => Widget(id: $"w{i}"))
            .ToArray();

        Assert.That(ProfileCanvasValidator.Validate(Write(widgets)), Does.Contain("non-spacer"));
    }

    [Test]
    public void Twenty_widgets_and_twenty_spacers_together_validate()
    {
        var widgets = Enumerable.Range(0, ProfileCanvasValidator.MaxWidgets)
            .Select(i => Widget(id: $"w{i}"))
            .Concat(Enumerable.Range(0, ProfileCanvasValidator.MaxSpacers)
                .Select(i => Widget(id: $"s{i}", type: ProfileCanvasValidator.SpacerType)))
            .ToArray();

        Assert.That(ProfileCanvasValidator.Validate(Write(widgets)), Is.Null);
    }

    [Test]
    public void More_than_twenty_spacers_is_rejected()
    {
        var widgets = Enumerable.Range(0, ProfileCanvasValidator.MaxSpacers + 1)
            .Select(i => Widget(id: $"s{i}", type: ProfileCanvasValidator.SpacerType))
            .ToArray();

        Assert.That(ProfileCanvasValidator.Validate(Write(widgets)), Does.Contain("spacers"));
    }

    [Test]
    public void More_than_two_card_widgets_is_rejected()
    {
        var widgets = Enumerable.Range(0, 3).Select(i => Widget(id: $"w{i}", card: true)).ToArray();

        Assert.That(ProfileCanvasValidator.Validate(Write(widgets)), Does.Contain("card"));
    }

    [Test]
    public void Two_card_widgets_validate()
    {
        var widgets = Enumerable.Range(0, 2).Select(i => Widget(id: $"w{i}", card: true)).ToArray();

        Assert.That(ProfileCanvasValidator.Validate(Write(widgets)), Is.Null);
    }

    [Test]
    public void An_infinite_height_is_rejected_by_name()
    {
        var problem = ProfileCanvasValidator.Validate(Write(Widget(h: double.PositiveInfinity)));

        Assert.That(problem, Does.Contain("widgets[0].h"));
    }

    [TestCase(double.NaN)]
    [TestCase(-1d)]
    [TestCase(1.5d)]
    public void A_non_integer_or_negative_coordinate_is_rejected(double x)
    {
        Assert.That(ProfileCanvasValidator.Validate(Write(Widget(x: x))), Does.Contain("widgets[0].x"));
    }

    [Test]
    public void A_widget_running_past_the_fourth_column_is_rejected()
    {
        Assert.That(ProfileCanvasValidator.Validate(Write(Widget(x: 3, w: 2, h: 1))), Does.Contain("column"));
    }

    [TestCase(3d, 1d)]
    [TestCase(1d, 2d)]
    [TestCase(4d, 3d)]
    public void A_footprint_that_is_not_on_the_list_is_rejected(double w, double h)
    {
        Assert.That(ProfileCanvasValidator.Validate(Write(Widget(w: w, h: h))), Does.Contain("footprint"));
    }

    [TestCase(1d, 1d)]
    [TestCase(2d, 1d)]
    [TestCase(2d, 2d)]
    [TestCase(4d, 1d)]
    [TestCase(4d, 2d)]
    public void Every_allowed_footprint_validates(double w, double h)
    {
        Assert.That(ProfileCanvasValidator.Validate(Write(Widget(w: w, h: h))), Is.Null);
    }

    [Test]
    public void An_unknown_visibility_is_rejected()
    {
        Assert.That(ProfileCanvasValidator.Validate(Write(Widget(visibility: "everybody"))),
            Does.Contain("visibility"));
    }

    [TestCase("everyone")]
    [TestCase("friends")]
    [TestCase("mutuals")]
    public void Each_known_visibility_validates(string visibility)
    {
        Assert.That(ProfileCanvasValidator.Validate(Write(Widget(visibility: visibility))), Is.Null);
    }

    [Test]
    public void A_config_over_the_cap_is_rejected()
    {
        var big = $$"""{"text":"{{new string('x', ProfileCanvasValidator.MaxConfigBytes)}}"}""";

        Assert.That(ProfileCanvasValidator.Validate(Write(Widget(config: big))), Does.Contain("config"));
    }

    [Test]
    public void A_duplicate_widget_id_is_rejected()
    {
        Assert.That(ProfileCanvasValidator.Validate(Write(Widget(), Widget(y: 1))), Does.Contain("duplicate"));
    }

    [Test]
    public void An_empty_widget_id_is_rejected()
    {
        Assert.That(ProfileCanvasValidator.Validate(Write(Widget(id: " "))), Does.Contain("widgets[0].id"));
    }

    [Test]
    public void A_non_hex_accent_is_rejected()
    {
        var dto = new CanvasWriteDto { Theme = new CanvasThemeDto { Accent = "red" }, Widgets = [] };

        Assert.That(ProfileCanvasValidator.Validate(dto), Does.Contain("theme.accent"));
    }

    [Test]
    public void An_image_backdrop_without_an_image_id_is_rejected()
    {
        var dto = new CanvasWriteDto
        {
            Theme = new CanvasThemeDto { Backdrop = new CanvasBackdropDto { Kind = "image" } },
            Widgets = [],
        };

        Assert.That(ProfileCanvasValidator.Validate(dto), Does.Contain("theme.backdrop.imageId"));
    }

    [Test]
    public void An_unknown_backdrop_kind_is_rejected()
    {
        var dto = new CanvasWriteDto
        {
            Theme = new CanvasThemeDto { Backdrop = new CanvasBackdropDto { Kind = "video" } },
            Widgets = [],
        };

        Assert.That(ProfileCanvasValidator.Validate(dto), Does.Contain("theme.backdrop.kind"));
    }
}
