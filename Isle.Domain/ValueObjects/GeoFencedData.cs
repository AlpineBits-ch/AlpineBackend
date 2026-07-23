using System.Numerics;
using Isle.Domain.Enums;

namespace Isle.Domain.ValueObjects;

public class GeoFenceData
{
    public GeoFenceShape Shape { get; set; }
    public Vector3 Center { get; set; }
    public float Radius { get; set; }              // used if Shape == Circle
    public List<Vector3> PolygonPoints { get; set; } = new(); // used if Shape == Polygon

    public bool Contains(Vector3 position)
    {
        return Shape switch
        {
            GeoFenceShape.Circle => Vector3.Distance(Center, position) <= Radius,
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