using System.Numerics;
using Isle.Domain.Enums;
using Isle.Domain.ValueObjects;

namespace Isle.Tests.Tests.Domain;

[TestFixture]
public class GeoFenceDataTests
{
    private static GeoFenceData Circle(Vector3 center, float radius) => new()
    {
        Shape = GeoFenceShape.Circle,
        Center = center,
        Radius = radius,
    };

    [Test]
    public void Contains_Circle_TrueWhenInsideRadius()
    {
        var fence = Circle(new Vector3(1000, 1000, 0), 500);

        Assert.That(fence.Contains(new Vector3(1200, 1000, 0)), Is.True);
    }

    [Test]
    public void Contains_Circle_TrueExactlyOnRadius()
    {
        var fence = Circle(new Vector3(0, 0, 0), 500);

        Assert.That(fence.Contains(new Vector3(500, 0, 0)), Is.True);
    }

    [Test]
    public void Contains_Circle_FalseJustOutsideRadius()
    {
        var fence = Circle(new Vector3(0, 0, 0), 500);

        Assert.That(fence.Contains(new Vector3(500.1f, 0, 0)), Is.False);
    }

    [Test]
    public void Contains_Circle_IgnoresElevation()
    {
        var fence = Circle(new Vector3(1000, 1000, 0), 500);

        // Same X,Y as a position well inside the zone, but 50,000 units up — a full 3D distance check
        // would put this far outside the radius; the ground-plane check must not care about Z.
        Assert.That(fence.Contains(new Vector3(1000, 1000, 50000)), Is.True);
    }

    [Test]
    public void Contains_Polygon_TrueForPointInsideASquare()
    {
        var fence = new GeoFenceData
        {
            Shape = GeoFenceShape.Polygon,
            PolygonPoints =
            [
                new Vector3(0, 0, 0),
                new Vector3(0, 0, 100),
                new Vector3(100, 0, 100),
                new Vector3(100, 0, 0),
            ],
        };

        Assert.That(fence.Contains(new Vector3(50, 0, 50)), Is.True);
    }

    [Test]
    public void Contains_Polygon_FalseForPointOutsideASquare()
    {
        var fence = new GeoFenceData
        {
            Shape = GeoFenceShape.Polygon,
            PolygonPoints =
            [
                new Vector3(0, 0, 0),
                new Vector3(0, 0, 100),
                new Vector3(100, 0, 100),
                new Vector3(100, 0, 0),
            ],
        };

        Assert.That(fence.Contains(new Vector3(500, 0, 500)), Is.False);
    }

    [Test]
    public void Contains_UnknownShape_IsAlwaysFalse()
    {
        var fence = new GeoFenceData { Shape = (GeoFenceShape)999 };

        Assert.That(fence.Contains(new Vector3(0, 0, 0)), Is.False);
    }

    [Test]
    public void Contains_Circle_ZeroRadius_OnlyExactCenterMatches()
    {
        var fence = Circle(new Vector3(1000, 1000, 0), 0);

        Assert.That(fence.Contains(new Vector3(1000, 1000, 0)), Is.True);
        Assert.That(fence.Contains(new Vector3(1000.1f, 1000, 0)), Is.False);
    }

    [Test]
    public void Contains_Circle_NegativeRadius_NeverMatchesAnything()
    {
        var fence = Circle(new Vector3(1000, 1000, 0), -50);

        Assert.That(fence.Contains(new Vector3(1000, 1000, 0)), Is.False);
    }

    [Test]
    public void Contains_Polygon_FewerThanThreePoints_IsAlwaysFalse()
    {
        var fence = new GeoFenceData
        {
            Shape = GeoFenceShape.Polygon,
            PolygonPoints = [new Vector3(0, 0, 0), new Vector3(10, 0, 10)],
        };

        Assert.That(fence.Contains(new Vector3(5, 0, 5)), Is.False);
    }
}
