using System.Numerics;
using Isle.Domain.Enums;

namespace Isle.Domain.ValueObjects;

public class GeoFenceData
{
    public GeoFenceShape Shape { get; set; }
    public Vector3 Center { get; set; }
    public float Radius { get; set; }
    public List<Vector3> PolygonPoints { get; set; } = new(); 

    public bool Contains(Vector3 position)
    {
        return Shape switch
        {
            // X,Y only: this is the same ground-plane convention RegionMap.Resolve and the world
            // roster use.
            GeoFenceShape.Circle => Vector2.Distance(
                new Vector2(Center.X, Center.Y), new Vector2(position.X, position.Y)) <= Radius,
            GeoFenceShape.Polygon => IsInPolygon(position, PolygonPoints),
            _ => false
        };
    }

    private static bool IsInPolygon(Vector3 point, List<Vector3> polygon)
    {
        // standard ray-casting point-in-polygon check (XZ plane)
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            bool intersects = (pi.Z > point.Z) != (pj.Z > point.Z) &&
                              point.X < (pj.X - pi.X) * (point.Z - pi.Z) / (pj.Z - pi.Z) + pi.X;
            if (intersects) inside = !inside;
        }
        return inside;
    }
}