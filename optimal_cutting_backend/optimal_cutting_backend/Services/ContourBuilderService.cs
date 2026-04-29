using System.Globalization;
using vega.Migrations.DAL;
using vega.Models;
using vega.Services.Interfaces;

namespace vega.Services
{
    public class ContourBuilderService : IContourBuilderService
    {
        private const float EndpointTolerance = 2.0f;
        private const float DuplicatePointTolerance = 0.05f;

        public void BuildGeometry(Detail2D detail)
        {
            if (detail.Figures == null || detail.Figures.Count == 0)
            {
                BuildRectangleGeometry(detail);
                return;
            }

            detail.InitializeBounds();

            var closedLoops = new List<List<Point2D>>();
            var openChains = new List<List<Point2D>>();

            foreach (var figure in detail.Figures)
            {
                var path = BuildPathFromFigure(figure);
                path = NormalizePath(path);

                if (path.Count < 2)
                {
                    continue;
                }

                if (IsClosed(path))
                {
                    closedLoops.Add(CloseLoop(path));
                }
                else
                {
                    openChains.Add(path);
                }
            }

            StitchOpenChains(openChains, closedLoops);

            if (openChains.Count > 0)
            {
                throw BuildContourException(detail, openChains, closedLoops, "Unclosed chains remained after stitching.");
            }

            closedLoops = closedLoops
                .Select(NormalizePath)
                .Select(CloseLoop)
                .Where(loop => loop.Count >= 4 && Math.Abs(SignedArea(loop)) > 0.01f)
                .ToList();

            if (closedLoops.Count == 0)
            {
                throw BuildContourException(detail, openChains, closedLoops, "No closed loops were built from DXF figures.");
            }

            var allLoopPoints = closedLoops.SelectMany(x => x).ToList();
            var minX = allLoopPoints.Min(p => p.X);
            var minY = allLoopPoints.Min(p => p.Y);

            var shiftedLoops = closedLoops
                .Select(loop => loop.Select(p => new Point2D(p.X - minX, p.Y - minY)).ToList())
                .ToList();

            var loopInfos = shiftedLoops
                .Select(loop => new LoopInfo
                {
                    Points = EnsureCounterClockwiseForOuter(loop),
                    SignedArea = SignedArea(loop),
                    AbsArea = Math.Abs(SignedArea(loop)),
                    Centroid = PolygonCentroid(loop)
                })
                .OrderByDescending(x => x.AbsArea)
                .ToList();

            var contour = new Contour2D();

            for (var i = 0; i < loopInfos.Count; i++)
            {
                var current = loopInfos[i];
                var depth = 0;

                for (var j = 0; j < loopInfos.Count; j++)
                {
                    if (i == j)
                    {
                        continue;
                    }

                    if (loopInfos[j].AbsArea <= current.AbsArea)
                    {
                        continue;
                    }

                    if (PointInPolygon(current.Centroid, loopInfos[j].Points))
                    {
                        depth++;
                    }
                }

                if (depth % 2 == 0)
                {
                    contour.FilledContours.Add(CloseLoop(current.Points));
                }
                else
                {
                    contour.HoleContours.Add(CloseLoop(EnsureClockwiseForHole(current.Points)));
                }
            }

            if (contour.FilledContours.Count == 0)
            {
                throw BuildContourException(detail, openChains, closedLoops, "Only holes were detected. Outer contour is missing.");
            }

            var contourPoints = contour.GetAllPoints().ToList();
            contour.MinX = 0;
            contour.MinY = 0;
            contour.MaxX = contourPoints.Max(p => p.X);
            contour.MaxY = contourPoints.Max(p => p.Y);

            detail.Contour = contour;
            detail.RotatedContour = RotateAndNormalize(contour);

            detail.MinX = detail.Figures.SelectMany(GetFigurePoints).Min(p => p.X);
            detail.MinY = detail.Figures.SelectMany(GetFigurePoints).Min(p => p.Y);
            detail.MaxX = detail.Figures.SelectMany(GetFigurePoints).Max(p => p.X);
            detail.MaxY = detail.Figures.SelectMany(GetFigurePoints).Max(p => p.Y);

            detail.OriginalWidth = Math.Max(1, (int)Math.Ceiling(contour.GetWidth()));
            detail.OriginalHeight = Math.Max(1, (int)Math.Ceiling(contour.GetHeight()));
            detail.RotatedWidth = Math.Max(1, (int)Math.Ceiling(detail.RotatedContour.GetWidth()));
            detail.RotatedHeight = Math.Max(1, (int)Math.Ceiling(detail.RotatedContour.GetHeight()));
            detail.Width = detail.OriginalWidth;
            detail.Height = detail.OriginalHeight;
        }

        private static InvalidOperationException BuildContourException(
            Detail2D detail,
            List<List<Point2D>> openChains,
            List<List<Point2D>> closedLoops,
            string reason)
        {
            var detailName = string.IsNullOrWhiteSpace(detail.Name) ? "(without name)" : detail.Name;
            return new InvalidOperationException(
                $"Failed to build closed contour for detail '{detailName}'. {reason} " +
                $"Figures={detail.Figures.Count}, ClosedLoops={closedLoops.Count}, OpenChains={openChains.Count}.");
        }

        private static void BuildRectangleGeometry(Detail2D detail)
        {
            detail.InitializeBounds();

            var contour = new Contour2D
            {
                FilledContours = new List<List<Point2D>>
                {
                    new()
                    {
                        new Point2D(0, 0),
                        new Point2D(detail.Width, 0),
                        new Point2D(detail.Width, detail.Height),
                        new Point2D(0, detail.Height),
                        new Point2D(0, 0)
                    }
                },
                HoleContours = new List<List<Point2D>>(),
                MinX = 0,
                MinY = 0,
                MaxX = detail.Width,
                MaxY = detail.Height
            };

            detail.Contour = contour;
            detail.RotatedContour = RotateAndNormalize(contour);
            detail.OriginalWidth = detail.Width;
            detail.OriginalHeight = detail.Height;
            detail.RotatedWidth = detail.Height;
            detail.RotatedHeight = detail.Width;
            detail.MinX = 0;
            detail.MinY = 0;
            detail.MaxX = detail.Width;
            detail.MaxY = detail.Height;
        }

        private static List<Point2D> BuildPathFromFigure(Figure figure)
        {
            return figure.TypeId switch
            {
                1 => BuildLinePath(figure),
                2 => BuildCirclePath(figure),
                3 => BuildArcPath(figure),
                4 => BuildSplinePath(figure),
                _ => new List<Point2D>()
            };
        }

        private static List<Point2D> BuildLinePath(Figure figure)
        {
            var coords = ParseCoords(figure.Coordinates);
            if (coords.Count < 4)
            {
                return new List<Point2D>();
            }

            return new List<Point2D>
            {
                new(coords[0], coords[1]),
                new(coords[2], coords[3])
            };
        }

        private static List<Point2D> BuildCirclePath(Figure figure)
        {
            var coords = ParseCoords(figure.Coordinates);
            if (coords.Count < 3)
            {
                return new List<Point2D>();
            }

            var cx = coords[0];
            var cy = coords[1];
            var r = coords[2];
            var segments = Math.Max(48, (int)Math.Ceiling(2 * Math.PI * r / 4f));

            var result = new List<Point2D>();
            for (var i = 0; i <= segments; i++)
            {
                var angle = (Math.PI * 2.0 * i) / segments;
                result.Add(new Point2D(
                    (float)(cx + r * Math.Cos(angle)),
                    (float)(cy + r * Math.Sin(angle))));
            }

            return result;
        }

        private static List<Point2D> BuildArcPath(Figure figure)
        {
            var coords = ParseCoords(figure.Coordinates);
            if (coords.Count < 5)
            {
                return new List<Point2D>();
            }

            var cx = coords[0];
            var cy = coords[1];
            var r = coords[2];
            var start = NormalizeAngle(coords[3]);
            var end = NormalizeAngle(coords[4]);

            var sweep = end - start;
            if (sweep <= 0)
            {
                sweep += 360f;
            }

            var segments = Math.Max(12, (int)Math.Ceiling(sweep / 5f));
            var result = new List<Point2D>();

            for (var i = 0; i <= segments; i++)
            {
                var angleDeg = start + (sweep * i / segments);
                var angleRad = angleDeg * Math.PI / 180.0;
                result.Add(new Point2D(
                    (float)(cx + r * Math.Cos(angleRad)),
                    (float)(cy + r * Math.Sin(angleRad))));
            }

            return result;
        }

        private static List<Point2D> BuildSplinePath(Figure figure)
        {
            var result = new List<Point2D>();
            var culture = new CultureInfo("ru-RU");
            var points = figure.Coordinates.Split('/', StringSplitOptions.RemoveEmptyEntries);

            foreach (var point in points)
            {
                var parts = point.Replace('.', ',').Split(';', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                result.Add(new Point2D(
                    float.Parse(parts[0], culture),
                    float.Parse(parts[1], culture)));
            }

            return result;
        }

        private static void StitchOpenChains(List<List<Point2D>> openChains, List<List<Point2D>> closedLoops)
        {
            var progress = true;

            while (progress)
            {
                progress = false;

                for (var i = 0; i < openChains.Count; i++)
                {
                    openChains[i] = NormalizePath(openChains[i]);

                    if (IsClosed(openChains[i]))
                    {
                        closedLoops.Add(CloseLoop(openChains[i]));
                        openChains.RemoveAt(i);
                        progress = true;
                        i--;
                        continue;
                    }

                    for (var j = i + 1; j < openChains.Count; j++)
                    {
                        if (!TryJoinChains(openChains[i], openChains[j], out var merged))
                        {
                            continue;
                        }

                        openChains[i] = NormalizePath(merged);
                        openChains.RemoveAt(j);
                        progress = true;
                        break;
                    }
                }
            }

            for (var i = openChains.Count - 1; i >= 0; i--)
            {
                openChains[i] = NormalizePath(openChains[i]);

                if (IsClosed(openChains[i]))
                {
                    closedLoops.Add(CloseLoop(openChains[i]));
                    openChains.RemoveAt(i);
                }
            }
        }

        private static bool TryJoinChains(List<Point2D> a, List<Point2D> b, out List<Point2D> merged)
        {
            merged = new List<Point2D>();

            var aStart = a[0];
            var aEnd = a[^1];
            var bStart = b[0];
            var bEnd = b[^1];

            if (Distance(aEnd, bStart) <= EndpointTolerance)
            {
                merged = a.Concat(b.Skip(1)).ToList();
                return true;
            }

            if (Distance(aEnd, bEnd) <= EndpointTolerance)
            {
                merged = a.Concat(b.Take(b.Count - 1).Reverse()).ToList();
                return true;
            }

            if (Distance(aStart, bEnd) <= EndpointTolerance)
            {
                merged = b.Concat(a.Skip(1)).ToList();
                return true;
            }

            if (Distance(aStart, bStart) <= EndpointTolerance)
            {
                merged = b.AsEnumerable().Reverse().Concat(a.Skip(1)).ToList();
                return true;
            }

            return false;
        }

        private static Contour2D RotateAndNormalize(Contour2D contour)
        {
            var rotated = new Contour2D
            {
                FilledContours = contour.FilledContours
                    .Select(loop => loop.Select(p => new Point2D(-p.Y, p.X)).ToList())
                    .ToList(),
                HoleContours = contour.HoleContours
                    .Select(loop => loop.Select(p => new Point2D(-p.Y, p.X)).ToList())
                    .ToList()
            };

            var points = rotated.GetAllPoints().ToList();
            var minX = points.Min(p => p.X);
            var minY = points.Min(p => p.Y);

            rotated.FilledContours = rotated.FilledContours
                .Select(loop => loop.Select(p => new Point2D(p.X - minX, p.Y - minY)).ToList())
                .ToList();

            rotated.HoleContours = rotated.HoleContours
                .Select(loop => loop.Select(p => new Point2D(p.X - minX, p.Y - minY)).ToList())
                .ToList();

            var normalizedPoints = rotated.GetAllPoints().ToList();
            rotated.MinX = 0;
            rotated.MinY = 0;
            rotated.MaxX = normalizedPoints.Max(p => p.X);
            rotated.MaxY = normalizedPoints.Max(p => p.Y);

            return rotated;
        }

        private static List<float> ParseCoords(string coordinates)
        {
            return coordinates
                .Replace('.', ',')
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => float.Parse(x, new CultureInfo("ru-RU")))
                .ToList();
        }

        private static IEnumerable<Point2D> GetFigurePoints(Figure figure)
        {
            return BuildPathFromFigure(figure);
        }

        private static List<Point2D> NormalizePath(List<Point2D> path)
        {
            var result = new List<Point2D>();

            foreach (var point in path)
            {
                if (result.Count == 0 || Distance(result[^1], point) > DuplicatePointTolerance)
                {
                    result.Add(point);
                }
            }

            return result;
        }

        private static bool IsClosed(List<Point2D> path)
        {
            return path.Count >= 3 && Distance(path[0], path[^1]) <= EndpointTolerance;
        }

        private static List<Point2D> CloseLoop(List<Point2D> path)
        {
            var result = NormalizePath(path);
            if (result.Count == 0)
            {
                return result;
            }

            if (Distance(result[0], result[^1]) > EndpointTolerance)
            {
                result.Add(result[0]);
            }
            else
            {
                result[^1] = result[0];
            }

            return result;
        }

        private static List<Point2D> EnsureCounterClockwiseForOuter(List<Point2D> loop)
        {
            return SignedArea(loop) >= 0 ? loop : loop.AsEnumerable().Reverse().ToList();
        }

        private static List<Point2D> EnsureClockwiseForHole(List<Point2D> loop)
        {
            return SignedArea(loop) <= 0 ? loop : loop.AsEnumerable().Reverse().ToList();
        }

        private static float SignedArea(List<Point2D> polygon)
        {
            if (polygon.Count < 3)
            {
                return 0;
            }

            double sum = 0;
            for (var i = 0; i < polygon.Count - 1; i++)
            {
                sum += polygon[i].X * polygon[i + 1].Y - polygon[i + 1].X * polygon[i].Y;
            }

            return (float)(sum / 2.0);
        }

        private static Point2D PolygonCentroid(List<Point2D> polygon)
        {
            if (polygon.Count < 3)
            {
                return polygon.Count > 0 ? polygon[0] : new Point2D(0, 0);
            }

            var area = SignedArea(polygon);
            if (Math.Abs(area) < 0.0001f)
            {
                return polygon[0];
            }

            double cx = 0;
            double cy = 0;

            for (var i = 0; i < polygon.Count - 1; i++)
            {
                var factor = polygon[i].X * polygon[i + 1].Y - polygon[i + 1].X * polygon[i].Y;
                cx += (polygon[i].X + polygon[i + 1].X) * factor;
                cy += (polygon[i].Y + polygon[i + 1].Y) * factor;
            }

            var divider = 6.0 * area;
            return new Point2D((float)(cx / divider), (float)(cy / divider));
        }

        private static bool PointInPolygon(Point2D point, List<Point2D> polygon)
        {
            var inside = false;

            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var xi = polygon[i].X;
                var yi = polygon[i].Y;
                var xj = polygon[j].X;
                var yj = polygon[j].Y;

                var intersect = ((yi > point.Y) != (yj > point.Y))
                                && (point.X < (xj - xi) * (point.Y - yi) / ((yj - yi) == 0 ? 0.000001f : (yj - yi)) + xi);

                if (intersect)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        private static float Distance(Point2D a, Point2D b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private static float NormalizeAngle(float angle)
        {
            return (angle % 360 + 360) % 360;
        }

        private sealed class LoopInfo
        {
            public List<Point2D> Points { get; set; } = new();
            public float SignedArea { get; set; }
            public float AbsArea { get; set; }
            public Point2D Centroid { get; set; }
        }
    }
}