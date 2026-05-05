using Clipper2Lib;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Valid;
using netDxf;
using netDxf.Entities;
using System.Text;
using vega.Controllers.DTO.Nesting;
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
    public NestingSolver(INfpService nfp, IPlacementCandidateGenerator candidates) { _nfp = nfp; _candidates = candidates; }

    public NestingResult Solve(List<NormalizedPolygon> sheets, List<NormalizedPolygon> parts, double kerf, double clearance, bool localSearch, IReadOnlyList<int>? rotations = null)
    {
        var res = new NestingResult { Sheets = sheets.Select(s => s.Id).ToList() };
        var gap = kerf + clearance;
        var angles = rotations is { Count: > 0 } ? rotations : new[] { 0, 90, 180, 270 };
        var placedBySheet = sheets.ToDictionary(s => s.Id, _ => new List<Geometry>());

        foreach (var part in parts)
        {
            var done = false;
            foreach (var sheet in sheets)
            {
                foreach (var angle in angles)
                {
                    var rotated = Rotate(part.Polygon, angle);
                    var anchor = GetAnchor(rotated);
                    var anchorNormalized = Translate(rotated, -anchor.X, -anchor.Y);
                    var inner = _nfp.BuildInnerNfp(sheet.Polygon, (Polygon)anchorNormalized, gap);
                    var forbiddens = placedBySheet[sheet.Id].Select(p => _nfp.BuildOuterNfp((Polygon)p, (Polygon)anchorNormalized, gap)).ToList();
                    var candidates = _candidates.GenerateCandidates(inner, forbiddens);

                    NestingPlacement? best = null;
                    foreach (var c in candidates)
                    {
                        var moved = Translate(anchorNormalized, c.X, c.Y);
                        if (!IsValidPlacement(sheet.Polygon, moved, placedBySheet[sheet.Id], gap)) continue;
                        if (best == null || IsBetter(moved, best.TransformedGeometry!))
                        {
                            best = new NestingPlacement { PartId = part.Id, SheetId = sheet.Id, X = c.X, Y = c.Y, Rotation = angle, TransformedGeometry = moved };
                        }
                    }

                    if (best != null)
                    {
                        placedBySheet[sheet.Id].Add(best.TransformedGeometry!);
                        res.PlacedParts.Add(best);
                        done = true;
                        break;
                    }
                }
                if (done) break;
            }
            if (!done) res.UnplacedParts.Add(part.Id);
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

    private static bool IsValidPlacement(Polygon sheet, Geometry moved, List<Geometry> placed, double gap)
    {
        if (!sheet.Covers(moved)) return false;
        for (var i = 0; i < sheet.NumInteriorRings; i++) if (sheet.GetInteriorRingN(i).Intersects(moved)) return false;
        foreach (var p in placed)
        {
            if (gap <= 0)
            {
                if (p.Intersects(moved)) return false;
            }
            else if (p.Distance(moved) < gap || p.Intersects(moved)) return false;
        }
        return true;
    }

    private static bool IsBetter(Geometry current, Geometry previous)
    {
        var ce = current.EnvelopeInternal; var pe = previous.EnvelopeInternal;
        var cArea = ce.Width * ce.Height;
        var pArea = pe.Width * pe.Height;
        if (Math.Abs(cArea - pArea) > 1e-6) return cArea < pArea;
        if (Math.Abs(ce.MinY - pe.MinY) > 1e-6) return ce.MinY < pe.MinY;
        return ce.MinX < pe.MinX;
    }
}

public class PolygonNestingService : IPolygonNestingService
{
    private readonly IGeometryNormalizer _normalizer; private readonly IPolygonValidator _validator; private readonly INestingSolver _solver;
    public PolygonNestingService(IGeometryNormalizer n, IPolygonValidator v, INestingSolver s) { _normalizer=n; _validator=v; _solver=s; }
    public NestingResult Nest(Cutting2DNestingDTO dto)
    {
        var sheets = dto.Sheets.Select(s => _normalizer.Normalize(s.Id, s.Outer, s.Holes, dto.Scale, true)).ToList();
        var parts = dto.Parts.SelectMany(p => Enumerable.Range(0, p.Quantity).Select(i => _normalizer.Normalize($"{p.Id}_{i+1}", p.Outer, p.Holes, dto.Scale, false))).ToList();
        sheets.ForEach(s => _validator.ValidatePolygon(s.Polygon, s.Id));
        parts.ForEach(p => _validator.ValidatePolygon(p.Polygon, p.Id));
        var res = _solver.Solve(sheets, parts, dto.Kerf, dto.Clearance, dto.EnableLocalSearch, dto.AllowedRotationsDegrees);

        res.Svg = BuildSvg(sheets, res.PlacedParts);
        var dxf = new DxfDocument();
        foreach (var p in res.PlacedParts.Where(x => x.TransformedGeometry is Polygon))
        {
            var poly = (Polygon)p.TransformedGeometry!;
            AddPolygonToDxf(dxf, poly);
        }
        using var ms = new MemoryStream(); dxf.Save(ms); res.Dxf = Convert.ToBase64String(ms.ToArray());
        return res;
    }

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
