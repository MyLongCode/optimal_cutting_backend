using NetTopologySuite.Geometries;
using vega.Models.Nesting;
using vega.Services.Interfaces.Nesting;

namespace vega.Services.Nesting;

public class RectangleNestingSolver : IRectangleNestingSolver
{
    private const double Eps = 1e-6;
    private readonly GeometryFactory _geometryFactory = new();

    private readonly record struct RectD(double X, double Y, double Width, double Height)
    {
        public double MaxX => X + Width;
        public double MaxY => Y + Height;
    }

    private sealed record RectPlacementState(
        string PartId,
        string SheetId,
        double X,
        double Y,
        double Width,
        double Height,
        int Rotation,
        Polygon Geometry);

    private sealed record PlacementCandidate(
        ClassifiedPart Part,
        NormalizedPolygon Sheet,
        double X,
        double Y,
        double Width,
        double Height,
        int Rotation,
        double OccupiedEnvelopeArea);

    public List<NestingPlacement> SolveRectangles(
        List<NormalizedPolygon> sheets,
        List<ClassifiedPart> rectangles,
        double gap,
        IReadOnlyList<int> rotations)
    {
        var angles = (rotations.Count > 0 ? rotations : new[] { 0, 90, 180, 270 })
            .Select(NormalizeAngle)
            .Distinct()
            .OrderBy(a => a)
            .ToList();

        var placedBySheet = sheets.ToDictionary(s => s.Id, _ => new List<RectPlacementState>());
        var placements = new List<NestingPlacement>();

        foreach (var part in rectangles
            .OrderByDescending(r => r.Width * r.Height)
            .ThenByDescending(r => r.Height)
            .ThenByDescending(r => r.Width)
            .ThenBy(r => r.Source.Id, StringComparer.Ordinal))
        {
            var candidate = FindBestCandidate(sheets, placedBySheet, part, gap, angles);
            if (candidate == null)
            {
                continue;
            }

            var geometry = BuildRectanglePolygon(_geometryFactory, candidate.X, candidate.Y, candidate.Width, candidate.Height);
            var state = new RectPlacementState(
                part.Source.Id,
                candidate.Sheet.Id,
                candidate.X,
                candidate.Y,
                candidate.Width,
                candidate.Height,
                candidate.Rotation,
                geometry);

            placedBySheet[candidate.Sheet.Id].Add(state);
            placements.Add(new NestingPlacement
            {
                PartId = state.PartId,
                SheetId = state.SheetId,
                X = state.X,
                Y = state.Y,
                Rotation = state.Rotation,
                TransformedGeometry = state.Geometry
            });
        }

        return placements
            .OrderBy(p => p.SheetId, StringComparer.Ordinal)
            .ThenBy(p => p.Y)
            .ThenBy(p => p.X)
            .ThenBy(p => p.PartId, StringComparer.Ordinal)
            .ToList();
    }

    private static PlacementCandidate? FindBestCandidate(
        List<NormalizedPolygon> sheets,
        Dictionary<string, List<RectPlacementState>> placedBySheet,
        ClassifiedPart part,
        double gap,
        IReadOnlyList<int> angles)
    {
        PlacementCandidate? best = null;

        foreach (var sheet in sheets)
        {
            var sheetEnv = sheet.Polygon.EnvelopeInternal;
            var placed = placedBySheet[sheet.Id];

            foreach (var angle in angles)
            {
                var (width, height) = GetRotatedSize(part.Width, part.Height, angle);
                if (width <= Eps || height <= Eps)
                {
                    continue;
                }

                foreach (var (x, y) in GenerateCandidatePositions(sheetEnv, placed, gap))
                {
                    var rect = new RectD(x, y, width, height);
                    if (!FitsWithin(sheetEnv, rect) || placed.Any(p => Intersects(rect, new RectD(p.X, p.Y, p.Width, p.Height), gap)))
                    {
                        continue;
                    }

                    var occupiedArea = CalculateOccupiedEnvelopeArea(placed, rect);
                    var candidate = new PlacementCandidate(part, sheet, x, y, width, height, angle, occupiedArea);
                    if (best == null || IsBetter(candidate, best))
                    {
                        best = candidate;
                    }
                }
            }
        }

        return best;
    }

    private static IEnumerable<(double X, double Y)> GenerateCandidatePositions(
        Envelope sheetEnv,
        List<RectPlacementState> placed,
        double gap)
    {
        var xs = new SortedSet<double> { sheetEnv.MinX };
        var ys = new SortedSet<double> { sheetEnv.MinY };

        foreach (var p in placed)
        {
            xs.Add(p.X);
            xs.Add(p.X + p.Width + gap);
            ys.Add(p.Y);
            ys.Add(p.Y + p.Height + gap);
        }

        foreach (var x in xs)
        foreach (var y in ys)
        {
            if (double.IsFinite(x) && double.IsFinite(y))
            {
                yield return (x, y);
            }
        }
    }

    private static bool FitsWithin(Envelope sheetEnv, RectD rect)
        => rect.X >= sheetEnv.MinX - Eps
           && rect.Y >= sheetEnv.MinY - Eps
           && rect.MaxX <= sheetEnv.MaxX + Eps
           && rect.MaxY <= sheetEnv.MaxY + Eps;

    private static bool Intersects(RectD a, RectD b, double gap)
        => a.X < b.X + b.Width + gap - Eps
           && a.X + a.Width + gap > b.X + Eps
           && a.Y < b.Y + b.Height + gap - Eps
           && a.Y + a.Height + gap > b.Y + Eps;

    private static double CalculateOccupiedEnvelopeArea(List<RectPlacementState> placed, RectD rect)
    {
        var minX = rect.X;
        var minY = rect.Y;
        var maxX = rect.MaxX;
        var maxY = rect.MaxY;

        foreach (var p in placed)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X + p.Width);
            maxY = Math.Max(maxY, p.Y + p.Height);
        }

        return Math.Max(0, maxX - minX) * Math.Max(0, maxY - minY);
    }

    private static bool IsBetter(PlacementCandidate current, PlacementCandidate previous)
    {
        var cmp = current.Y.CompareTo(previous.Y);
        if (cmp != 0) return cmp < 0;

        cmp = current.X.CompareTo(previous.X);
        if (cmp != 0) return cmp < 0;

        cmp = current.OccupiedEnvelopeArea.CompareTo(previous.OccupiedEnvelopeArea);
        if (cmp != 0) return cmp < 0;

        cmp = string.CompareOrdinal(current.Sheet.Id, previous.Sheet.Id);
        if (cmp != 0) return cmp < 0;

        cmp = current.Rotation.CompareTo(previous.Rotation);
        if (cmp != 0) return cmp < 0;

        return string.CompareOrdinal(current.Part.Source.Id, previous.Part.Source.Id) < 0;
    }

    private static (double Width, double Height) GetRotatedSize(double width, double height, int angle)
        => NormalizeAngle(angle) is 90 or 270 ? (height, width) : (width, height);

    private static int NormalizeAngle(int angle)
    {
        var normalized = angle % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static Polygon BuildRectanglePolygon(
        GeometryFactory gf,
        double x,
        double y,
        double width,
        double height)
        => gf.CreatePolygon(new[]
        {
            new Coordinate(x, y),
            new Coordinate(x + width, y),
            new Coordinate(x + width, y + height),
            new Coordinate(x, y + height),
            new Coordinate(x, y)
        });
}
