using Clipper2Lib;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Valid;
using netDxf;
using netDxf.Entities;
using System.Text;
using System.Text.Json;
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
    public Geometry BuildOuterNfp(Polygon a, Polygon b, double offset) => a.Buffer(offset).Union(b.Buffer(offset));
    public Geometry BuildInnerNfp(Polygon sheet, Polygon part, double offset) => sheet.Buffer(-offset).Difference(part.Buffer(offset));
}

public class PlacementCandidateGenerator : IPlacementCandidateGenerator
{
    public IEnumerable<Coordinate> GenerateCandidates(Geometry innerNfp)
    {
        if (innerNfp is Polygon p) return p.Coordinates;
        return innerNfp.Coordinates;
    }
}

public class NestingSolver : INestingSolver
{
    private readonly INfpService _nfp;
    private readonly IPlacementCandidateGenerator _candidates;
    public NestingSolver(INfpService nfp, IPlacementCandidateGenerator candidates) { _nfp = nfp; _candidates = candidates; }
    public NestingResult Solve(List<NormalizedPolygon> sheets, List<NormalizedPolygon> parts, double kerf, double clearance, bool localSearch)
    {
        var res = new NestingResult { Sheets = sheets.Select(s => s.Id).ToList() };
        var placed = new List<Geometry>();
        foreach (var part in parts)
        {
            var done = false;
            foreach (var sheet in sheets)
            {
                var inner = _nfp.BuildInnerNfp(sheet.Polygon, part.Polygon, kerf + clearance);
                foreach (var c in _candidates.GenerateCandidates(inner))
                {
                    var moved = (Geometry)part.Polygon.Copy();
                    moved.Apply(new NetTopologySuite.Geometries.Utilities.AffineTransformation(1,0,c.X,0,1,c.Y));
                    if (!sheet.Polygon.Covers(moved)) continue;
                    if (placed.Any(p => p.Intersects(moved))) continue;
                    placed.Add(moved);
                    res.PlacedParts.Add(new NestingPlacement { PartId = part.Id, SheetId = sheet.Id, X = c.X, Y = c.Y, TransformedGeometry = moved });
                    done = true; break;
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
        var res = _solver.Solve(sheets, parts, dto.Kerf, dto.Clearance, dto.EnableLocalSearch);
        res.Svg = "<svg><!-- polygon contours --></svg>";
        var dxf = new DxfDocument();
        foreach (var p in res.PlacedParts.Where(x => x.TransformedGeometry is Polygon))
        {
            var poly = (Polygon)p.TransformedGeometry!;
            var coords = poly.ExteriorRing.Coordinates;
            for (int i = 0; i < coords.Length - 1; i++) dxf.Entities.Add(new Line(new netDxf.Vector2(coords[i].X, coords[i].Y), new netDxf.Vector2(coords[i+1].X, coords[i+1].Y)));
        }
        using var ms = new MemoryStream(); dxf.Save(ms); res.Dxf = Convert.ToBase64String(ms.ToArray());
        return res;
    }
}
