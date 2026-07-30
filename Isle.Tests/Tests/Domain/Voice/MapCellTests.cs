using Isle.Domain.Entity.Voice;

namespace Isle.Tests.Tests.Domain.Voice;

[TestFixture]
public class MapCellTests
{
    private const float CellSize = 3000f;

    [Test]
    public void CellX_CellY_FlooredTowardsNegativeInfinity()
    {
        var cell = new MapCell { WorldX = -1f, WorldY = -1f, CellSize = CellSize };

        // -1 / 3000 = -0.000333.. -> floor is -1, not 0 (truncation would be wrong here).
        Assert.That(cell.CellX, Is.EqualTo(-1));
        Assert.That(cell.CellY, Is.EqualTo(-1));
    }

    [Test]
    public void CellX_CellY_PositiveWorldCoordinates()
    {
        var cell = new MapCell { WorldX = 6500f, WorldY = 3200f, CellSize = CellSize };

        Assert.That(cell.CellX, Is.EqualTo(2));
        Assert.That(cell.CellY, Is.EqualTo(1));
    }

    [Test]
    public void Equals_SameCellIndices_AreEqualEvenWithDifferentWorldCoordinates()
    {
        // Two points anywhere inside the same cell must hash/compare equal - equality is
        // index-based, not coordinate-based.
        var a = new MapCell { WorldX = 100f, WorldY = 100f, CellSize = CellSize };
        var b = new MapCell { WorldX = 2999f, WorldY = 2999f, CellSize = CellSize };

        Assert.That(a, Is.EqualTo(b));
        Assert.That(a == b, Is.True);
        Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
    }

    [Test]
    public void Equals_DifferentCellIndices_AreNotEqual()
    {
        var a = new MapCell { WorldX = 0f, WorldY = 0f, CellSize = CellSize };
        var b = new MapCell { WorldX = 3000f, WorldY = 0f, CellSize = CellSize };

        Assert.That(a, Is.Not.EqualTo(b));
        Assert.That(a != b, Is.True);
    }

    [Test]
    public void Neighbourhood_ReturnsNineCellsCenteredOnSelf()
    {
        var centre = new MapCell { WorldX = 4500f, WorldY = 4500f, CellSize = CellSize };

        var neighbourhood = centre.Neighbourhood().ToList();

        Assert.That(neighbourhood, Has.Count.EqualTo(9));
        Assert.That(neighbourhood, Has.Some.Matches<MapCell>(c => c.Equals(centre)), "Self must be included in its own neighbourhood");
    }

    [Test]
    public void Neighbourhood_ContainsAllEightAdjacentCells()
    {
        var centre = new MapCell { WorldX = 4500f, WorldY = 4500f, CellSize = CellSize }; // cell (1,1)

        var indices = centre.Neighbourhood().Select(c => (c.CellX, c.CellY)).ToHashSet();

        var expected = new HashSet<(int, int)>
        {
            (0, 0), (0, 1), (0, 2),
            (1, 0), (1, 1), (1, 2),
            (2, 0), (2, 1), (2, 2),
        };
        Assert.That(indices, Is.EquivalentTo(expected));
    }

    [Test]
    public void ToString_FormatsAsCellXUnderscoreCellY()
    {
        var cell = new MapCell { WorldX = 4500f, WorldY = 4500f, CellSize = CellSize };

        Assert.That(cell.ToString(), Is.EqualTo("1_1"));
    }
}
