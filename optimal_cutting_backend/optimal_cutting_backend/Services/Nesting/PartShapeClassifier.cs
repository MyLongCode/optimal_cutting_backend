using NetTopologySuite.Geometries;
using vega.Models.Nesting;
using vega.Services.Interfaces.Nesting;

namespace vega.Services.Nesting;

public class PartShapeClassifier : IPartShapeClassifier
{
    private const double Eps = 1e-6;

    public ClassifiedPart Classify(NormalizedPolygon part)
    {
        var env = part.Polygon.EnvelopeInternal;
        if (!IsRectangle(part.Polygon))
        {
            return new ClassifiedPart(part, NestingShapeKind.Complex, 0, 0);
        }

        return new ClassifiedPart(part, NestingShapeKind.Rectangle, env.Width, env.Height);
    }

    private static bool IsRectangle(Polygon polygon)
    {
        if (polygon.NumInteriorRings > 0)
        {
            return false;
        }

        var coords = polygon.ExteriorRing.Coordinates
            .Take(Math.Max(0, polygon.ExteriorRing.Coordinates.Length - 1))
            .Where(c => double.IsFinite(c.X) && double.IsFinite(c.Y))
            .ToList();

        coords = RemoveCollinearPoints(coords);

        if (coords.Count != 4)
        {
            return false;
        }

        var env = polygon.EnvelopeInternal;
        var envelopeArea = env.Width * env.Height;

        if (envelopeArea <= Eps)
        {
            return false;
        }

        if (Math.Abs(polygon.Area - envelopeArea) > Eps)
        {
            return false;
        }

        return coords.All(c =>
            (Math.Abs(c.X - env.MinX) <= Eps || Math.Abs(c.X - env.MaxX) <= Eps) &&
            (Math.Abs(c.Y - env.MinY) <= Eps || Math.Abs(c.Y - env.MaxY) <= Eps));
    }

    private static List<Coordinate> RemoveCollinearPoints(List<Coordinate> coords)
    {
        if (coords.Count <= 3)
        {
            return coords;
        }

        var changed = true;
        while (changed && coords.Count > 3)
        {
            changed = false;
            var filtered = new List<Coordinate>();

            for (var i = 0; i < coords.Count; i++)
            {
                var prev = coords[(i - 1 + coords.Count) % coords.Count];
                var current = coords[i];
                var next = coords[(i + 1) % coords.Count];

                if (IsDuplicate(current, prev) || IsCollinear(prev, current, next))
                {
                    changed = true;
                    continue;
                }

                filtered.Add(current);
            }

            coords = filtered;
        }

        return coords;
    }

    private static bool IsDuplicate(Coordinate a, Coordinate b)
        => Math.Abs(a.X - b.X) <= Eps && Math.Abs(a.Y - b.Y) <= Eps;

    private static bool IsCollinear(Coordinate a, Coordinate b, Coordinate c)
        => Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) <= Eps;
}
