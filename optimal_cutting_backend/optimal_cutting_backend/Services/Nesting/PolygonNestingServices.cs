using Clipper2Lib;
using NetTopologySuite.Geometries;
using System.Globalization;
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
    private const int LocalSearchIterations = 5;

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

    private readonly record struct LayoutScore(
        double TotalOccupiedEnvelopeArea,
        double MaxOccupiedHeight,
        double MaxOccupiedWidth,
        double ContactPenalty,
        double SameKindSpreadPenalty,
        double BottomLeftPenalty);

    public NestingSolver(INfpService nfp, IPlacementCandidateGenerator candidates) { _nfp = nfp; _candidates = candidates; }

    public NestingResult Solve(
        List<NormalizedPolygon> sheets,
        List<NormalizedPolygon> parts,
        double kerf,
        double clearance,
        bool localSearch,
        IReadOnlyList<int>? rotations = null,
        Dictionary<string, List<Geometry>>? initialPlacedBySheet = null,
        List<NestingPlacement>? initialPlacements = null)
    {
        var res = new NestingResult { Sheets = sheets.Select(s => s.Id).ToList() };
        if (initialPlacements != null)
        {
            res.PlacedParts.AddRange(initialPlacements.Select(ClonePlacement));
        }

        var gap = Math.Max(0, kerf + clearance);
        var angles = (rotations is { Count: > 0 } ? rotations : new[] { 0, 90, 180, 270 })
            .Select(NormalizeAngle)
            .Distinct()
            .ToList();
        var placedBySheet = sheets.ToDictionary(
            s => s.Id,
            s => initialPlacedBySheet != null && initialPlacedBySheet.TryGetValue(s.Id, out var initial)
                ? initial.Select(g => (Geometry)g.Copy()).ToList()
                : new List<Geometry>());

        foreach (var placement in res.PlacedParts.Where(p => p.TransformedGeometry != null))
        {
            if (!placedBySheet.TryGetValue(placement.SheetId, out var placed))
            {
                continue;
            }

            var alreadyPresent = placed.Any(g => ReferenceEquals(g, placement.TransformedGeometry) || g.EqualsExact(placement.TransformedGeometry));
            if (!alreadyPresent)
            {
                placed.Add((Geometry)placement.TransformedGeometry!.Copy());
            }
        }

        foreach (var part in parts.OrderByDescending(p => p.Polygon.Area))
        {
            var bestAcrossSheets = FindBestGreedyPlacement(part, sheets, gap, angles, placedBySheet);

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

        if (localSearch && res.PlacedParts.Count > 1)
        {
            res.PlacedParts = ImproveLayout(sheets, parts, res.PlacedParts, gap, angles);
            res.PlacedParts = CompactLayout(sheets, parts, res.PlacedParts, gap, angles);
        }

        foreach (var sheet in sheets)
        {
            var area = res.PlacedParts.Where(p => p.SheetId == sheet.Id).Sum(p => p.TransformedGeometry?.Area ?? 0);
            res.UtilizationBySheet[sheet.Id] = sheet.Polygon.Area == 0 ? 0 : area / sheet.Polygon.Area;
        }
        res.TotalUtilization = res.UtilizationBySheet.Count == 0 ? 0 : res.UtilizationBySheet.Values.Average();
        return res;
    }

    private NestingPlacement? FindBestGreedyPlacement(
        NormalizedPolygon part,
        List<NormalizedPolygon> sheets,
        double gap,
        IReadOnlyList<int> angles,
        Dictionary<string, List<Geometry>> placedBySheet)
    {
        NestingPlacement? bestAcrossSheets = null;
        PlacementScore? bestScore = null;

        foreach (var placement in EnumerateValidPlacements(part, sheets, gap, angles, placedBySheet))
        {
            var currentScore = ScorePlacement(placement.TransformedGeometry!, placedBySheet[placement.SheetId]);

            if (bestAcrossSheets == null || bestScore == null || IsBetterPlacement(placement, currentScore, bestAcrossSheets, bestScore.Value))
            {
                bestScore = currentScore;
                bestAcrossSheets = placement;
            }
        }

        return bestAcrossSheets;
    }

    private List<NestingPlacement> ImproveLayout(
        List<NormalizedPolygon> sheets,
        List<NormalizedPolygon> parts,
        List<NestingPlacement> initialPlacements,
        double gap,
        IReadOnlyList<int> angles)
    {
        var partById = parts.ToDictionary(p => p.Id, p => p);
        var current = initialPlacements.Select(ClonePlacement).ToList();
        var currentScore = ScoreLayout(sheets, current, gap);

        for (var iteration = 0; iteration < LocalSearchIterations; iteration++)
        {
            var improved = false;

            // Move large parts first: early bad decisions usually dominate the occupied envelope.
            var moveOrder = current
                .Where(p => p.TransformedGeometry != null && partById.ContainsKey(p.PartId))
                .OrderByDescending(p => p.TransformedGeometry!.Area)
                .ThenBy(p => p.PartId, StringComparer.Ordinal)
                .Select(p => p.PartId)
                .ToList();

            foreach (var partId in moveOrder)
            {
                var part = partById[partId];
                var fixedPlacements = current.Where(p => p.PartId != partId).Select(ClonePlacement).ToList();
                var fixedBySheet = BuildPlacedGeometryBySheet(sheets, fixedPlacements);

                NestingPlacement? bestCandidate = null;
                LayoutScore? bestCandidateScore = null;

                foreach (var candidate in EnumerateValidPlacements(part, sheets, gap, angles, fixedBySheet))
                {
                    var trial = fixedPlacements.Concat(new[] { candidate }).ToList();
                    var trialScore = ScoreLayout(sheets, trial, gap);

                    if (bestCandidate == null || bestCandidateScore == null || IsBetterLayoutScore(trialScore, bestCandidateScore.Value))
                    {
                        bestCandidate = candidate;
                        bestCandidateScore = trialScore;
                    }
                }

                if (bestCandidate != null && bestCandidateScore != null && IsBetterLayoutScore(bestCandidateScore.Value, currentScore))
                {
                    current = fixedPlacements.Concat(new[] { bestCandidate }).ToList();
                    currentScore = bestCandidateScore.Value;
                    improved = true;
                }
            }

            if (!improved) break;
        }

        return current
            .OrderBy(p => p.SheetId, StringComparer.Ordinal)
            .ThenBy(p => p.TransformedGeometry?.EnvelopeInternal.MinY ?? 0)
            .ThenBy(p => p.TransformedGeometry?.EnvelopeInternal.MinX ?? 0)
            .ToList();
    }

    private List<NestingPlacement> CompactLayout(
        List<NormalizedPolygon> sheets,
        List<NormalizedPolygon> parts,
        List<NestingPlacement> initialPlacements,
        double gap,
        IReadOnlyList<int> angles)
    {
        var partById = parts.ToDictionary(p => p.Id, p => p);
        var current = initialPlacements.Select(ClonePlacement).ToList();
        var currentScore = ScoreLayout(sheets, current, gap);

        // A few deterministic passes are enough: each pass removes one part and reinserts it
        // with the current global score, so previously frozen greedy choices can be corrected.
        for (var pass = 0; pass < 3; pass++)
        {
            var improved = false;
            var order = current
                .Where(p => p.TransformedGeometry != null && partById.ContainsKey(p.PartId))
                .OrderBy(p => p.TransformedGeometry!.EnvelopeInternal.MinY)
                .ThenBy(p => p.TransformedGeometry!.EnvelopeInternal.MinX)
                .Select(p => p.PartId)
                .ToList();

            foreach (var partId in order)
            {
                var original = current.First(p => p.PartId == partId);
                var part = partById[partId];
                var fixedPlacements = current.Where(p => p.PartId != partId).Select(ClonePlacement).ToList();
                var fixedBySheet = BuildPlacedGeometryBySheet(sheets, fixedPlacements);

                // In compaction, keep the current rotation first, but allow the configured rotations as fallback.
                var compactAngles = new[] { original.Rotation }
                    .Concat(angles)
                    .Select(NormalizeAngle)
                    .Distinct()
                    .ToList();

                NestingPlacement? bestCandidate = null;
                LayoutScore? bestCandidateScore = null;

                foreach (var candidate in EnumerateValidPlacements(part, sheets, gap, compactAngles, fixedBySheet))
                {
                    var trial = fixedPlacements.Concat(new[] { candidate }).ToList();
                    var trialScore = ScoreLayout(sheets, trial, gap);

                    if (bestCandidate == null || bestCandidateScore == null || IsBetterLayoutScore(trialScore, bestCandidateScore.Value))
                    {
                        bestCandidate = candidate;
                        bestCandidateScore = trialScore;
                    }
                }

                if (bestCandidate != null && bestCandidateScore != null && IsBetterLayoutScore(bestCandidateScore.Value, currentScore))
                {
                    current = fixedPlacements.Concat(new[] { bestCandidate }).ToList();
                    currentScore = bestCandidateScore.Value;
                    improved = true;
                }
            }

            if (!improved) break;
        }

        return current
            .OrderBy(p => p.SheetId, StringComparer.Ordinal)
            .ThenBy(p => p.TransformedGeometry?.EnvelopeInternal.MinY ?? 0)
            .ThenBy(p => p.TransformedGeometry?.EnvelopeInternal.MinX ?? 0)
            .ToList();
    }

    private IEnumerable<NestingPlacement> EnumerateValidPlacements(
        NormalizedPolygon part,
        List<NormalizedPolygon> sheets,
        double gap,
        IReadOnlyList<int> angles,
        Dictionary<string, List<Geometry>> placedBySheet)
    {
        foreach (var sheet in sheets)
        {
            foreach (var angle in angles)
            {
                var rotated = Rotate(part.Polygon, angle);
                var anchor = GetAnchor(rotated);
                var anchorNormalized = Translate(rotated, -anchor.X, -anchor.Y);
                var inner = _nfp.BuildInnerNfp(sheet.Polygon, (Polygon)anchorNormalized, gap);
                var placedOnSheet = placedBySheet.TryGetValue(sheet.Id, out var existing) ? existing : new List<Geometry>();
                var forbiddens = placedOnSheet.Select(p => _nfp.BuildOuterNfp((Polygon)p, (Polygon)anchorNormalized, gap)).ToList();
                var candidates = _candidates.GenerateCandidates(inner, forbiddens)
                    .Concat(BuildFallbackCandidates(sheet.Polygon))
                    .Concat(BuildEnvelopeCandidates(sheet.Polygon, anchorNormalized, placedOnSheet, gap))
                    .Where(IsFiniteCoordinate)
                    .OrderBy(c => c.Y)
                    .ThenBy(c => c.X)
                    .DistinctBy(c => $"{Math.Round(c.X, 6)}:{Math.Round(c.Y, 6)}")
                    .ToList();

                foreach (var c in candidates)
                {
                    var moved = Translate(anchorNormalized, c.X, c.Y);
                    if (!IsValidPlacement(sheet.Polygon, moved, placedOnSheet, gap)) continue;

                    yield return new NestingPlacement
                    {
                        PartId = part.Id,
                        SheetId = sheet.Id,
                        X = c.X,
                        Y = c.Y,
                        Rotation = angle,
                        TransformedGeometry = moved
                    };
                }
            }
        }
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

    private static LayoutScore ScoreLayout(List<NormalizedPolygon> sheets, List<NestingPlacement> placements, double gap)
    {
        var totalOccupiedEnvelopeArea = 0d;
        var maxOccupiedHeight = 0d;
        var maxOccupiedWidth = 0d;
        var contactPenalty = 0d;
        var bottomLeftPenalty = 0d;

        foreach (var sheet in sheets)
        {
            var placedOnSheet = placements
                .Where(p => p.SheetId == sheet.Id && p.TransformedGeometry != null)
                .ToList();

            if (placedOnSheet.Count == 0) continue;

            var occupied = new Envelope(placedOnSheet[0].TransformedGeometry!.EnvelopeInternal);
            foreach (var placement in placedOnSheet.Skip(1)) occupied.ExpandToInclude(placement.TransformedGeometry!.EnvelopeInternal);

            totalOccupiedEnvelopeArea += occupied.Width * occupied.Height;
            maxOccupiedHeight = Math.Max(maxOccupiedHeight, occupied.Height);
            maxOccupiedWidth = Math.Max(maxOccupiedWidth, occupied.Width);

            foreach (var placement in placedOnSheet)
            {
                var geom = placement.TransformedGeometry!;
                var env = geom.EnvelopeInternal;
                bottomLeftPenalty += env.MinY * 1e-3 + env.MinX * 1e-6;

                var nearestDistance = geom.Distance(sheet.Polygon.Boundary);
                foreach (var other in placedOnSheet)
                {
                    if (ReferenceEquals(placement, other)) continue;
                    nearestDistance = Math.Min(nearestDistance, geom.Distance(other.TransformedGeometry!));
                }

                // Distance values are in normalized units; only a tiny weight is needed.
                // A lower value means more contact with the sheet edge or neighboring parts.
                contactPenalty += Math.Max(0, nearestDistance - gap) * 1e-3;
            }
        }

        var sameKindSpreadPenalty = ScoreSameKindSpread(placements) * 1e-3;

        return new LayoutScore(
            TotalOccupiedEnvelopeArea: totalOccupiedEnvelopeArea,
            MaxOccupiedHeight: maxOccupiedHeight,
            MaxOccupiedWidth: maxOccupiedWidth,
            ContactPenalty: contactPenalty,
            SameKindSpreadPenalty: sameKindSpreadPenalty,
            BottomLeftPenalty: bottomLeftPenalty);
    }

    private static double ScoreSameKindSpread(List<NestingPlacement> placements)
    {
        var penalty = 0d;

        foreach (var group in placements
            .Where(p => p.TransformedGeometry != null)
            .GroupBy(p => GetBasePartId(p.PartId)))
        {
            var items = group.ToList();
            if (items.Count < 2) continue;

            var env = new Envelope(items[0].TransformedGeometry!.EnvelopeInternal);
            foreach (var item in items.Skip(1)) env.ExpandToInclude(item.TransformedGeometry!.EnvelopeInternal);
            penalty += env.Width * env.Height;
        }

        return penalty;
    }

    private static bool IsBetterLayoutScore(LayoutScore current, LayoutScore previous)
    {
        const double eps = 1e-6;

        if (Math.Abs(current.TotalOccupiedEnvelopeArea - previous.TotalOccupiedEnvelopeArea) > eps)
            return current.TotalOccupiedEnvelopeArea < previous.TotalOccupiedEnvelopeArea;

        if (Math.Abs(current.MaxOccupiedHeight - previous.MaxOccupiedHeight) > eps)
            return current.MaxOccupiedHeight < previous.MaxOccupiedHeight;

        if (Math.Abs(current.MaxOccupiedWidth - previous.MaxOccupiedWidth) > eps)
            return current.MaxOccupiedWidth < previous.MaxOccupiedWidth;

        if (Math.Abs(current.SameKindSpreadPenalty - previous.SameKindSpreadPenalty) > eps)
            return current.SameKindSpreadPenalty < previous.SameKindSpreadPenalty;

        if (Math.Abs(current.ContactPenalty - previous.ContactPenalty) > eps)
            return current.ContactPenalty < previous.ContactPenalty;

        return current.BottomLeftPenalty < previous.BottomLeftPenalty;
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

    private static Dictionary<string, List<Geometry>> BuildPlacedGeometryBySheet(List<NormalizedPolygon> sheets, IEnumerable<NestingPlacement> placements)
        => sheets.ToDictionary(
            s => s.Id,
            s => placements
                .Where(p => p.SheetId == s.Id && p.TransformedGeometry != null)
                .Select(p => p.TransformedGeometry!)
                .ToList());

    private static NestingPlacement ClonePlacement(NestingPlacement placement)
        => new()
        {
            PartId = placement.PartId,
            SheetId = placement.SheetId,
            X = placement.X,
            Y = placement.Y,
            Rotation = placement.Rotation,
            Contours = placement.Contours,
            TransformedGeometry = placement.TransformedGeometry == null ? null : (Geometry)placement.TransformedGeometry.Copy()
        };

    private static string GetBasePartId(string partId)
    {
        var index = partId.LastIndexOf('_');
        return index <= 0 ? partId : partId[..index];
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
    private readonly IGeometryNormalizer _normalizer;
    private readonly IPolygonValidator _validator;
    private readonly INestingSolver _solver;
    private readonly IPartShapeClassifier _shapeClassifier;
    private readonly IRectangleNestingSolver _rectangleSolver;

    public PolygonNestingService(
        IGeometryNormalizer normalizer,
        IPolygonValidator validator,
        INestingSolver solver,
        IPartShapeClassifier shapeClassifier,
        IRectangleNestingSolver rectangleSolver)
    {
        _normalizer = normalizer;
        _validator = validator;
        _solver = solver;
        _shapeClassifier = shapeClassifier;
        _rectangleSolver = rectangleSolver;
    }

    public NestingResult Nest(Cutting2DNestingDTO dto)
    {
        if (dto.Scale <= 0) throw new ArgumentException("scale must be greater than zero");

        var sheets = dto.Sheets.Select(s => _normalizer.Normalize(s.Id, s.Outer, s.Holes, dto.Scale, true)).ToList();
        var parts = dto.Parts.SelectMany(p => Enumerable.Range(0, p.Quantity).Select(i => _normalizer.Normalize($"{p.Id}_{i+1}", p.Outer, p.Holes, dto.Scale, false))).ToList();
        sheets.ForEach(s => _validator.ValidatePolygon(s.Polygon, s.Id));
        parts.ForEach(p => _validator.ValidatePolygon(p.Polygon, p.Id));

        var kerf = dto.Kerf * dto.Scale;
        var clearance = dto.Clearance * dto.Scale;
        var gap = Math.Max(0, kerf + clearance);
        var rotations = dto.AllowedRotationsDegrees is { Count: > 0 }
            ? dto.AllowedRotationsDegrees
            : new List<int> { 0, 90, 180, 270 };

        var res = dto.EnableRectangleFastPath
            ? SolveWithRectangleFastPath(sheets, parts, kerf, clearance, gap, dto.EnableLocalSearch, rotations)
            : _solver.Solve(sheets, parts, kerf, clearance, dto.EnableLocalSearch, dto.AllowedRotationsDegrees);

        return BuildOutput(res, sheets, dto.Scale);
    }

    private NestingResult SolveWithRectangleFastPath(
        List<NormalizedPolygon> sheets,
        List<NormalizedPolygon> parts,
        double kerf,
        double clearance,
        double gap,
        bool localSearch,
        IReadOnlyList<int> rotations)
    {
        var classifiedSheets = sheets.Select(_shapeClassifier.Classify).ToList();
        if (classifiedSheets.Any(s => s.Kind != NestingShapeKind.Rectangle))
        {
            return _solver.Solve(sheets, parts, kerf, clearance, localSearch, rotations);
        }

        var classifiedParts = parts.Select(_shapeClassifier.Classify).ToList();
        var rectangles = classifiedParts.Where(p => p.Kind == NestingShapeKind.Rectangle).ToList();
        var complexParts = classifiedParts.Where(p => p.Kind == NestingShapeKind.Complex).Select(p => p.Source).ToList();

        if (rectangles.Count == 0)
        {
            return _solver.Solve(sheets, parts, kerf, clearance, localSearch, rotations);
        }

        var rectanglePlacements = _rectangleSolver.SolveRectangles(sheets, rectangles, gap, rotations);
        var placedRectangleIds = rectanglePlacements.Select(p => p.PartId).ToHashSet(StringComparer.Ordinal);
        var unplacedRectangleIds = rectangles
            .Select(r => r.Source.Id)
            .Where(id => !placedRectangleIds.Contains(id))
            .ToList();

        if (complexParts.Count == 0)
        {
            var result = new NestingResult
            {
                Sheets = sheets.Select(s => s.Id).ToList(),
                PlacedParts = rectanglePlacements,
                UnplacedParts = unplacedRectangleIds
            };

            PopulateUtilization(result, sheets);
            return result;
        }

        var initialPlacedBySheet = rectanglePlacements
            .Where(p => p.TransformedGeometry != null)
            .GroupBy(p => p.SheetId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => (Geometry)p.TransformedGeometry!.Copy()).ToList());

        var mixedResult = _solver.Solve(
            sheets,
            complexParts,
            kerf,
            clearance,
            localSearch,
            rotations,
            initialPlacedBySheet,
            rectanglePlacements);

        mixedResult.UnplacedParts.AddRange(unplacedRectangleIds);
        PopulateUtilization(mixedResult, sheets);
        return mixedResult;
    }

    private static void PopulateUtilization(NestingResult result, List<NormalizedPolygon> sheets)
    {
        foreach (var sheet in sheets)
        {
            var area = result.PlacedParts.Where(p => p.SheetId == sheet.Id).Sum(p => p.TransformedGeometry?.Area ?? 0);
            result.UtilizationBySheet[sheet.Id] = sheet.Polygon.Area == 0 ? 0 : area / sheet.Polygon.Area;
        }

        result.TotalUtilization = result.UtilizationBySheet.Count == 0 ? 0 : result.UtilizationBySheet.Values.Average();
    }

    private static NestingResult BuildOutput(NestingResult res, List<NormalizedPolygon> sheets, int scale)
    {
        var outputSheets = sheets.Select(s => s with { Polygon = (Polygon)ScaleGeometry(s.Polygon, 1.0 / scale) }).ToList();
        foreach (var placement in res.PlacedParts)
        {
            placement.X /= scale;
            placement.Y /= scale;
            if (placement.TransformedGeometry != null)
            {
                placement.TransformedGeometry = ScaleGeometry(placement.TransformedGeometry, 1.0 / scale);
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
        var polygons = sheets.Select(s => s.Polygon)
            .Concat(placements.Select(p => p.TransformedGeometry).OfType<Polygon>())
            .ToList();

        var bounds = polygons.Count == 0
            ? new SvgBounds(0, 0, 1, 1)
            : SvgBounds.FromPolygons(polygons);

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' width='{Fmt(bounds.Width)}' height='{Fmt(bounds.Height)}' viewBox='{Fmt(bounds.MinX)} {Fmt(bounds.MinY)} {Fmt(bounds.Width)} {Fmt(bounds.Height)}' preserveAspectRatio='xMidYMid meet'>");
        foreach (var s in sheets) sb.Append(PathForPolygon(s.Polygon, "fill:none;stroke:#666;stroke-width:1"));
        foreach (var p in placements.Where(x => x.TransformedGeometry is Polygon)) sb.Append(PathForPolygon((Polygon)p.TransformedGeometry!, "fill:rgba(0,128,255,0.2);stroke:#0080ff;stroke-width:1"));
        sb.Append("</svg>");
        return sb.ToString();
    }

    private readonly record struct SvgBounds(double MinX, double MinY, double Width, double Height)
    {
        public static SvgBounds FromPolygons(IEnumerable<Polygon> polygons)
        {
            var minX = double.PositiveInfinity;
            var minY = double.PositiveInfinity;
            var maxX = double.NegativeInfinity;
            var maxY = double.NegativeInfinity;

            foreach (var polygon in polygons)
            {
                var env = polygon.EnvelopeInternal;
                minX = Math.Min(minX, env.MinX);
                minY = Math.Min(minY, env.MinY);
                maxX = Math.Max(maxX, env.MaxX);
                maxY = Math.Max(maxY, env.MaxY);
            }

            if (!double.IsFinite(minX) || !double.IsFinite(minY) || !double.IsFinite(maxX) || !double.IsFinite(maxY))
            {
                return new SvgBounds(0, 0, 1, 1);
            }

            return new SvgBounds(minX, minY, Math.Max(maxX - minX, 1), Math.Max(maxY - minY, 1));
        }
    }

    private static string PathForPolygon(Polygon poly, string style)
    {
        var d = RingToPath(poly.ExteriorRing.Coordinates);
        for (var i = 0; i < poly.NumInteriorRings; i++) d += RingToPath(poly.GetInteriorRingN(i).Coordinates);
        return $"<path d='{d}' style='{style}'/>";
    }

    private static string RingToPath(Coordinate[] coords)
        => "M " + string.Join(" L ", coords.Take(coords.Length - 1).Select(c => $"{Fmt(c.X)},{Fmt(c.Y)}")) + " Z ";

    private static string Fmt(double value)
        => value.ToString("0.################", CultureInfo.InvariantCulture);
}
