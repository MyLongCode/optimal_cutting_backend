using Clipper2Lib;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Valid;
using netDxf;
using netDxf.Entities;
using System.Text;
using vega.Controllers.DTO.Nesting;
using vega.Models;
using vega.Models.Nesting;
using vega.Services.Interfaces.Nesting;

namespace vega.Services.Nesting;

public class GeometryNormalizer : IGeometryNormalizer
{
    private readonly GeometryFactory _gf = new();
    public NormalizedPolygon Normalize(string id, List<List<NestingPointDto>> outer, List<List<List<NestingPointDto>>> holes, int scale, bool isSheet)
    {
        var shell = ToRing(outer.First(), scale);
        var inner = holes.Select(h => ToRing(h.First(), scale)).ToArray();
        return new NormalizedPolygon(id, _gf.CreatePolygon(shell, inner), isSheet);
    }

    private static LinearRing ToRing(List<NestingPointDto> points, int scale)
    {
        var coords = points.Select(p => new Coordinate(Math.Round(p.X * scale), Math.Round(p.Y * scale))).ToList();
        if (!coords.First().Equals2D(coords.Last())) coords.Add(coords.First());
        return new LinearRing(coords.ToArray());
    }
}

public class PolygonValidator : IPolygonValidator
{
    public void ValidatePolygon(Polygon polygon, string id)
    {
        if (polygon.IsEmpty || polygon.NumPoints < 4) throw new ArgumentException($"{id} invalid polygon");
        var err = new IsValidOp(polygon).ValidationError;
        if (err != null) throw new ArgumentException($"{id} invalid: {err.Message}");
    }
}

public class NfpService : INfpService
{
    private readonly GeometryFactory _gf = new();

    public Geometry BuildOuterNfp(Polygon fixedPart, Polygon movingPartAtAnchorZero, double offset)
    {
        var inflatedMoving = (Polygon)movingPartAtAnchorZero.Buffer(offset);
        var reflected = ReflectAroundOrigin(inflatedMoving.ExteriorRing.Coordinates);
        var nfpPaths = Clipper.MinkowskiSum(reflected, ToPathD(fixedPart.ExteriorRing.Coordinates), true);
        return PathsToGeometry(nfpPaths);
    }

    public Geometry BuildInnerNfp(Polygon sheet, Polygon movingPartAtAnchorZero, double offset)
    {
        var inflatedMoving = (Polygon)movingPartAtAnchorZero.Buffer(offset);
        var reflected = ReflectAroundOrigin(inflatedMoving.ExteriorRing.Coordinates);
        var outer = Clipper.MinkowskiDiff(ToPathD(sheet.ExteriorRing.Coordinates), reflected, true);
        Geometry allowed = PathsToGeometry(outer);

        for (var i = 0; i < sheet.NumInteriorRings; i++)
        {
            var hole = (LinearRing)sheet.GetInteriorRingN(i);
            var holePoly = (Polygon)sheet.Factory.CreatePolygon((LinearRing)hole.Copy());
            var forbiddenByHole = PathsToGeometry(Clipper.MinkowskiSum(reflected, ToPathD(holePoly.ExteriorRing.Coordinates), true));
            allowed = allowed.Difference(forbiddenByHole);
        }
        return allowed;
    }

    private Geometry PathsToGeometry(PathsD paths)
    {
        if (paths.Count == 0) return _gf.CreateGeometryCollection();
        var polys = new List<Polygon>();
        foreach (var p in paths)
        {
            if (p.Count < 3) continue;
            var coords = p.Select(pt => new Coordinate(pt.x, pt.y)).ToList();
            if (!coords.First().Equals2D(coords.Last())) coords.Add(coords.First());
            polys.Add(_gf.CreatePolygon(coords.ToArray()));
        }
        return polys.Count switch
        {
            0 => _gf.CreateGeometryCollection(),
            1 => polys[0],
            _ => _gf.CreateMultiPolygon(polys.ToArray())
        };
    }

    private static PathD ToPathD(Coordinate[] coords)
        => new(coords.Take(coords.Length - 1).Select(c => new PointD(c.X, c.Y)));

    private static PathD ReflectAroundOrigin(Coordinate[] coords)
        => new(coords.Take(coords.Length - 1).Select(c => new PointD(-c.X, -c.Y)));
}

public class PlacementCandidateGenerator : IPlacementCandidateGenerator
{
    public IEnumerable<Coordinate> GenerateCandidates(Geometry innerNfp, IEnumerable<Geometry>? forbidden = null)
    {
        var result = new List<Coordinate>();
        AddGeometryPoints(innerNfp, result);
        if (forbidden != null)
        {
            foreach (var geom in forbidden) AddGeometryPoints(geom, result);
        }
        return result
            .OrderBy(c => c.Y)
            .ThenBy(c => c.X)
            .DistinctBy(c => $"{Math.Round(c.X, 6)}:{Math.Round(c.Y, 6)}");
    }

    private static void AddGeometryPoints(Geometry g, List<Coordinate> acc)
    {
        acc.AddRange(g.Coordinates);
        if (g is Polygon p)
        {
            acc.AddRange(p.ExteriorRing.Coordinates);
            for (var i = 0; i < p.NumInteriorRings; i++) acc.AddRange(p.GetInteriorRingN(i).Coordinates);
        }
    }
}

public class NestingSolver : INestingSolver
{
    private readonly INfpService _nfp;
    private readonly IPlacementCandidateGenerator _candidates;

    private readonly record struct PlacementScore(
        double OccupiedArea,
        double OccupiedMaxY,
        double OccupiedMaxX,
        bool FitsInsideOccupiedEnvelope,
        double ExpansionArea,
        double PartMaxY,
        double PartMinY,
        double PartMinX);

    public NestingSolver(INfpService nfp, IPlacementCandidateGenerator candidates) { _nfp = nfp; _candidates = candidates; }

    public NestingResult Solve(List<NormalizedPolygon> sheets, List<NormalizedPolygon> parts, double kerf, double clearance, bool localSearch, IReadOnlyList<int>? rotations = null)
    {
        var res = new NestingResult { Sheets = sheets.Select(s => s.Id).ToList() };
        var gap = Math.Max(0, kerf + clearance);
        var angles = (rotations is { Count: > 0 } ? rotations : new[] { 0, 90, 180, 270 })
            .Select(NormalizeAngle)
            .Distinct()
            .ToList();
        var placedBySheet = sheets.ToDictionary(s => s.Id, _ => new List<Geometry>());

        foreach (var part in parts.OrderByDescending(p => p.Polygon.Area))
        {
            NestingPlacement? bestAcrossSheets = null;
            PlacementScore? bestScore = null;

            foreach (var sheet in sheets)
            {
                foreach (var angle in angles)
                {
                    var rotated = Rotate(part.Polygon, angle);
                    var anchor = GetAnchor(rotated);
                    var anchorNormalized = Translate(rotated, -anchor.X, -anchor.Y);
                    var inner = _nfp.BuildInnerNfp(sheet.Polygon, (Polygon)anchorNormalized, gap);
                    var forbiddens = placedBySheet[sheet.Id].Select(p => _nfp.BuildOuterNfp((Polygon)p, (Polygon)anchorNormalized, gap)).ToList();
                    var candidates = _candidates.GenerateCandidates(inner, forbiddens)
                        .Concat(BuildFallbackCandidates(sheet.Polygon))
                        .Concat(BuildEnvelopeCandidates(sheet.Polygon, anchorNormalized, placedBySheet[sheet.Id], gap))
                        .Where(IsFiniteCoordinate)
                        .OrderBy(c => c.Y)
                        .ThenBy(c => c.X)
                        .DistinctBy(c => $"{Math.Round(c.X, 6)}:{Math.Round(c.Y, 6)}")
                        .ToList();

                    foreach (var c in candidates)
                    {
                        var moved = Translate(anchorNormalized, c.X, c.Y);
                        if (!IsValidPlacement(sheet.Polygon, moved, placedBySheet[sheet.Id], gap)) continue;

                        var currentScore = ScorePlacement(moved, placedBySheet[sheet.Id]);
                        var placement = new NestingPlacement { PartId = part.Id, SheetId = sheet.Id, X = c.X, Y = c.Y, Rotation = angle, TransformedGeometry = moved };

                        if (bestAcrossSheets == null || bestScore == null || IsBetterPlacement(placement, currentScore, bestAcrossSheets, bestScore.Value))
                        {
                            bestScore = currentScore;
                            bestAcrossSheets = placement;
                        }
                    }
                }
            }

            if (bestAcrossSheets != null)
            {
                placedBySheet[bestAcrossSheets.SheetId].Add(bestAcrossSheets.TransformedGeometry!);
                res.PlacedParts.Add(bestAcrossSheets);
            }
            else
            {
                res.UnplacedParts.Add(part.Id);
            }
        }

        foreach (var sheet in sheets)
        {
            var area = res.PlacedParts.Where(p => p.SheetId == sheet.Id).Sum(p => p.TransformedGeometry?.Area ?? 0);
            res.UtilizationBySheet[sheet.Id] = sheet.Polygon.Area == 0 ? 0 : area / sheet.Polygon.Area;
        }
        res.TotalUtilization = res.UtilizationBySheet.Count == 0 ? 0 : res.UtilizationBySheet.Values.Average();
        return res;
    }

    private static Geometry Rotate(Geometry g, int angle)
    {
        var t = AffineTransformation.RotationInstance(Math.PI * angle / 180.0);
        var c = (Geometry)g.Copy(); c.Apply(t); return c;
    }
    private static Geometry Translate(Geometry g, double x, double y)
    {
        var t = AffineTransformation.TranslationInstance(x, y);
        var c = (Geometry)g.Copy(); c.Apply(t); return c;
    }
    private static Coordinate GetAnchor(Geometry g) => g.Coordinates.OrderBy(c => c.X).ThenBy(c => c.Y).First();

    private static bool IsFiniteCoordinate(Coordinate coordinate)
        => double.IsFinite(coordinate.X) && double.IsFinite(coordinate.Y);

    private static bool IsValidPlacement(Polygon sheet, Geometry moved, List<Geometry> placed, double gap)
    {
        if (!sheet.Covers(moved)) return false;
        for (var i = 0; i < sheet.NumInteriorRings; i++) if (sheet.GetInteriorRingN(i).Intersects(moved)) return false;
        foreach (var p in placed)
        {
            if (gap <= 0)
            {
                if (p.Intersection(moved).Area > 1e-6) return false;
            }
            else if (p.Distance(moved) < gap) return false;
        }
        return true;
    }

    private static bool IsBetterPlacement(NestingPlacement current, PlacementScore currentScore, NestingPlacement previous, PlacementScore previousScore)
    {
        if (current.SheetId != previous.SheetId) return string.CompareOrdinal(current.SheetId, previous.SheetId) < 0;
        return IsBetterScore(currentScore, previousScore);
    }

    private static PlacementScore ScorePlacement(Geometry moved, List<Geometry> placed)
    {
        const double eps = 1e-6;

        var movedEnv = moved.EnvelopeInternal;
        var occupied = new Envelope(movedEnv.MinX, movedEnv.MaxX, movedEnv.MinY, movedEnv.MaxY);
        Envelope? previousOccupied = null;

        if (placed.Count > 0)
        {
            previousOccupied = new Envelope(placed[0].EnvelopeInternal);
            for (var i = 1; i < placed.Count; i++)
            {
                previousOccupied.ExpandToInclude(placed[i].EnvelopeInternal);
            }

            occupied = new Envelope(previousOccupied);
            occupied.ExpandToInclude(movedEnv);
        }

        var previousArea = previousOccupied == null ? 0 : previousOccupied.Width * previousOccupied.Height;
        var occupiedArea = occupied.Width * occupied.Height;
        var fitsInsideOccupiedEnvelope = previousOccupied != null
            && movedEnv.MinX >= previousOccupied.MinX - eps
            && movedEnv.MaxX <= previousOccupied.MaxX + eps
            && movedEnv.MinY >= previousOccupied.MinY - eps
            && movedEnv.MaxY <= previousOccupied.MaxY + eps;

        return new PlacementScore(
            OccupiedArea: occupiedArea,
            OccupiedMaxY: occupied.MaxY,
            OccupiedMaxX: occupied.MaxX,
            FitsInsideOccupiedEnvelope: fitsInsideOccupiedEnvelope,
            ExpansionArea: Math.Max(0, occupiedArea - previousArea),
            PartMaxY: movedEnv.MaxY,
            PartMinY: movedEnv.MinY,
            PartMinX: movedEnv.MinX);
    }

    private static bool IsBetterScore(PlacementScore current, PlacementScore previous)
    {
        const double eps = 1e-6;

        if (Math.Abs(current.OccupiedArea - previous.OccupiedArea) > eps) return current.OccupiedArea < previous.OccupiedArea;

        if (current.FitsInsideOccupiedEnvelope != previous.FitsInsideOccupiedEnvelope)
        {
            return current.FitsInsideOccupiedEnvelope;
        }

        if (Math.Abs(current.OccupiedMaxY - previous.OccupiedMaxY) > eps) return current.OccupiedMaxY < previous.OccupiedMaxY;
        if (Math.Abs(current.OccupiedMaxX - previous.OccupiedMaxX) > eps) return current.OccupiedMaxX < previous.OccupiedMaxX;

        if (current.FitsInsideOccupiedEnvelope && previous.FitsInsideOccupiedEnvelope)
        {
            if (Math.Abs(current.PartMaxY - previous.PartMaxY) > eps) return current.PartMaxY > previous.PartMaxY;
            if (Math.Abs(current.PartMinX - previous.PartMinX) > eps) return current.PartMinX < previous.PartMinX;
            return current.PartMinY < previous.PartMinY;
        }

        if (Math.Abs(current.ExpansionArea - previous.ExpansionArea) > eps) return current.ExpansionArea < previous.ExpansionArea;
        if (Math.Abs(current.PartMinY - previous.PartMinY) > eps) return current.PartMinY < previous.PartMinY;
        return current.PartMinX < previous.PartMinX;
    }

    private static IEnumerable<Coordinate> BuildFallbackCandidates(Polygon sheet)
    {
        var coords = new List<Coordinate>();
        coords.AddRange(sheet.ExteriorRing.Coordinates);
        for (var i = 0; i < sheet.NumInteriorRings; i++) coords.AddRange(sheet.GetInteriorRingN(i).Coordinates);
        var env = sheet.EnvelopeInternal;
        coords.Add(new Coordinate(env.MinX, env.MinY));
        coords.Add(new Coordinate(env.MinX, env.MaxY));
        coords.Add(new Coordinate(env.MaxX, env.MinY));
        coords.Add(new Coordinate(env.MaxX, env.MaxY));
        return coords
            .OrderBy(c => c.Y)
            .ThenBy(c => c.X)
            .DistinctBy(c => $"{Math.Round(c.X, 6)}:{Math.Round(c.Y, 6)}");
    }

    private static IEnumerable<Coordinate> BuildEnvelopeCandidates(Polygon sheet, Geometry anchorNormalized, List<Geometry> placed, double gap)
    {
        var sheetEnv = sheet.EnvelopeInternal;
        var movingEnv = anchorNormalized.EnvelopeInternal;
        var xs = new List<double> { sheetEnv.MinX - movingEnv.MinX };
        var ys = new List<double> { sheetEnv.MinY - movingEnv.MinY };

        foreach (var p in placed)
        {
            var env = p.EnvelopeInternal;
            xs.Add(env.MinX - movingEnv.MaxX - gap);
            xs.Add(env.MaxX - movingEnv.MinX + gap);
            ys.Add(env.MinY - movingEnv.MaxY - gap);
            ys.Add(env.MaxY - movingEnv.MinY + gap);
        }

        foreach (var x in xs)
        foreach (var y in ys)
            yield return new Coordinate(x, y);
    }

    private static int NormalizeAngle(int angle)
    {
        var normalized = angle % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }
}

public class PolygonNestingService : IPolygonNestingService
{
    private readonly IGeometryNormalizer _normalizer; private readonly IPolygonValidator _validator; private readonly INestingSolver _solver;
    public PolygonNestingService(IGeometryNormalizer n, IPolygonValidator v, INestingSolver s) { _normalizer=n; _validator=v; _solver=s; }
    public NestingResult Nest(Cutting2DNestingDTO dto)
    {
        if (dto.Scale <= 0) throw new ArgumentException("scale must be greater than zero");

        var sheets = dto.Sheets.Select(s => _normalizer.Normalize(s.Id, s.Outer, s.Holes, dto.Scale, true)).ToList();
        var parts = dto.Parts.SelectMany(p => Enumerable.Range(0, p.Quantity).Select(i => _normalizer.Normalize($"{p.Id}_{i+1}", p.Outer, p.Holes, dto.Scale, false))).ToList();
        sheets.ForEach(s => _validator.ValidatePolygon(s.Polygon, s.Id));
        parts.ForEach(p => _validator.ValidatePolygon(p.Polygon, p.Id));
        var res = _solver.Solve(sheets, parts, dto.Kerf * dto.Scale, dto.Clearance * dto.Scale, dto.EnableLocalSearch, dto.AllowedRotationsDegrees);

        var outputSheets = sheets.Select(s => s with { Polygon = (Polygon)ScaleGeometry(s.Polygon, 1.0 / dto.Scale) }).ToList();
        foreach (var placement in res.PlacedParts)
        {
            placement.X /= dto.Scale;
            placement.Y /= dto.Scale;
            if (placement.TransformedGeometry != null)
            {
                placement.TransformedGeometry = ScaleGeometry(placement.TransformedGeometry, 1.0 / dto.Scale);
                placement.Contours = BuildPlacementContours(placement.TransformedGeometry);
            }
        }

        res.Workpieces = BuildWorkpieces(outputSheets, res.PlacedParts, res.UtilizationBySheet);
        res.Svg = BuildSvg(outputSheets, res.PlacedParts);
        var dxf = new DxfDocument();
        foreach (var p in res.PlacedParts.Where(x => x.TransformedGeometry is Polygon))
        {
            var poly = (Polygon)p.TransformedGeometry!;
            AddPolygonToDxf(dxf, poly);
        }
        using var ms = new MemoryStream(); dxf.Save(ms); res.Dxf = Convert.ToBase64String(ms.ToArray());
        return res;
    }

    private static Geometry ScaleGeometry(Geometry geometry, double scale)
    {
        var transform = AffineTransformation.ScaleInstance(scale, scale);
        var copy = (Geometry)geometry.Copy();
        copy.Apply(transform);
        return copy;
    }

    private static List<List<NestingOutputPoint>> BuildPlacementContours(Geometry geometry)
    {
        if (geometry is not Polygon poly) return new List<List<NestingOutputPoint>>();

        var contours = new List<List<NestingOutputPoint>>
        {
            ToOutputRing(poly.ExteriorRing.Coordinates)
        };

        for (var i = 0; i < poly.NumInteriorRings; i++)
        {
            contours.Add(ToOutputRing(poly.GetInteriorRingN(i).Coordinates));
        }

        return contours;
    }

    private static List<NestingOutputPoint> ToOutputRing(Coordinate[] coords)
        => coords
            .Take(coords.Length - 1)
            .Where(c => double.IsFinite(c.X) && double.IsFinite(c.Y))
            .Select(c => new NestingOutputPoint { X = c.X, Y = c.Y })
            .ToList();

    private static List<Workpiece2D> BuildWorkpieces(List<NormalizedPolygon> sheets, List<NestingPlacement> placements, Dictionary<string, double> utilizationBySheet)
        => sheets.Select(sheet =>
        {
            var env = sheet.Polygon.EnvelopeInternal;
            return new Workpiece2D
            {
                Width = Math.Max(1, (int)Math.Ceiling(env.Width)),
                Height = Math.Max(1, (int)Math.Ceiling(env.Height)),
                ProcentUsage = utilizationBySheet.GetValueOrDefault(sheet.Id),
                Details = placements
                    .Where(p => p.SheetId == sheet.Id && p.TransformedGeometry is Polygon)
                    .Select(p => BuildDetail(p, env.MinX, env.MinY))
                    .ToList()
            };
        }).ToList();

    private static Detail2D BuildDetail(NestingPlacement placement, double offsetX, double offsetY)
    {
        var polygon = (Polygon)placement.TransformedGeometry!;
        var env = polygon.EnvelopeInternal;
        var width = Math.Max(1, (int)Math.Ceiling(env.Width));
        var height = Math.Max(1, (int)Math.Ceiling(env.Height));
        var x = env.MinX - offsetX;
        var y = env.MinY - offsetY;

        return new Detail2D
        {
            X = (float)x,
            Y = (float)y,
            Width = width,
            Height = height,
            OriginalWidth = width,
            OriginalHeight = height,
            RotatedWidth = height,
            RotatedHeight = width,
            Rotated = placement.Rotation == 90 || placement.Rotation == 270,
            Name = placement.PartId,
            MinX = (float)x,
            MinY = (float)y,
            MaxX = (float)(env.MaxX - offsetX),
            MaxY = (float)(env.MaxY - offsetY),
            Contour = BuildContour(polygon, offsetX, offsetY)
        };
    }

    private static Contour2D BuildContour(Polygon polygon, double offsetX, double offsetY)
    {
        var env = polygon.EnvelopeInternal;
        return new Contour2D
        {
            FilledContours = new List<List<Point2D>> { ToPoint2DRing(polygon.ExteriorRing.Coordinates, offsetX, offsetY) },
            HoleContours = Enumerable.Range(0, polygon.NumInteriorRings)
                .Select(i => ToPoint2DRing(polygon.GetInteriorRingN(i).Coordinates, offsetX, offsetY))
                .ToList(),
            MinX = (float)(env.MinX - offsetX),
            MinY = (float)(env.MinY - offsetY),
            MaxX = (float)(env.MaxX - offsetX),
            MaxY = (float)(env.MaxY - offsetY)
        };
    }

    private static List<Point2D> ToPoint2DRing(Coordinate[] coords, double offsetX, double offsetY)
        => coords
            .Take(coords.Length - 1)
            .Where(c => double.IsFinite(c.X) && double.IsFinite(c.Y))
            .Select(c => new Point2D((float)(c.X - offsetX), (float)(c.Y - offsetY)))
            .ToList();

    private static void AddPolygonToDxf(DxfDocument dxf, Polygon poly)
    {
        AddRing(dxf, poly.ExteriorRing.Coordinates);
        for (var i = 0; i < poly.NumInteriorRings; i++) AddRing(dxf, poly.GetInteriorRingN(i).Coordinates);
    }

    private static void AddRing(DxfDocument dxf, Coordinate[] coords)
    {
        for (int i = 0; i < coords.Length - 1; i++) dxf.Entities.Add(new Line(new netDxf.Vector2(coords[i].X, coords[i].Y), new netDxf.Vector2(coords[i+1].X, coords[i+1].Y)));
    }

    private static string BuildSvg(List<NormalizedPolygon> sheets, List<NestingPlacement> placements)
    {
        var sb = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg'>");
        foreach (var s in sheets) sb.Append(PathForPolygon(s.Polygon, "fill:none;stroke:#666;stroke-width:1"));
        foreach (var p in placements.Where(x => x.TransformedGeometry is Polygon)) sb.Append(PathForPolygon((Polygon)p.TransformedGeometry!, "fill:rgba(0,128,255,0.2);stroke:#0080ff;stroke-width:1"));
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string PathForPolygon(Polygon poly, string style)
    {
        var d = RingToPath(poly.ExteriorRing.Coordinates);
        for (var i = 0; i < poly.NumInteriorRings; i++) d += RingToPath(poly.GetInteriorRingN(i).Coordinates);
        return $"<path d='{d}' style='{style}'/>";
    }

    private static string RingToPath(Coordinate[] coords)
        => "M " + string.Join(" L ", coords.Take(coords.Length - 1).Select(c => $"{c.X},{-c.Y}")) + " Z ";
}
