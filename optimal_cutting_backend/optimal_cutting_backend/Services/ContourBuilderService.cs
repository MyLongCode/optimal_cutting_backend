using System.Globalization;
using vega.Migrations.DAL;
using vega.Models;
using vega.Services.Interfaces;

namespace vega.Services
{
    public class ContourBuilderService : IContourBuilderService
    {
        private const float JoinTolerance = 0.5f;

        public void BuildGeometry(Detail2D detail)
        {
            if (detail.Figures == null || detail.Figures.Count == 0)
            {
                BuildRectangleGeometry(detail);
                return;
            }

            var rawPaths = new List<List<Point2D>>();
            foreach (var figure in detail.Figures)
            {
                var path = BuildPathFromFigure(figure);
                if (path.Count >= 2)
                {
                    rawPaths.Add(path);
                }
            }

            if (rawPaths.Count == 0)
            {
                BuildRectangleGeometry(detail);
                return;
            }

            var closedLoops = new List<List<Point2D>>();
            var openPaths = new List<List<Point2D>>();

            foreach (var path in rawPaths)
            {
                var normalized = NormalizePath(path);
                if (normalized.Count < 2)
                {
                    continue;
                }

                if (IsClosed(normalized))
                {
                    closedLoops.Add(CloseLoop(normalized));
                }
                else
                {
                    openPaths.Add(normalized);
                }
            }

            MergeOpenPaths(openPaths, closedLoops);

            if (closedLoops.Count == 0)
            {
                BuildRectangleGeometry(detail);
                return;
            }

            var loops = closedLoops
                .Select(CloseLoop)
                .Where(x => x.Count >= 4)
                .ToList();

            if (loops.Count == 0)
            {
                BuildRectangleGeometry(detail);
                return;
            }

            var allPoints = loops.SelectMany(x => x).ToList();
            var minX = allPoints.Min(p => p.X);
            var minY = allPoints.Min(p => p.Y);

            var shiftedLoops = loops
                .Select(loop => loop.Select(p => new Point2D(p.X - minX, p.Y - minY)).ToList())
                .ToList();

            var loopInfos = shiftedLoops
                .Select(loop => new LoopInfo
                {
                    Points = loop,
                    Area = SignedArea(loop),
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
                    contour.FilledContours.Add(current.Points);
                }
                else
                {
                    contour.HoleContours.Add(current.Points);
                }
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
            var segments = Math.Max(32, (int)Math.Ceiling(2 * Math.PI * r / 5f));

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

            var segments = Math.Max(8, (int)Math.Ceiling(sweep / 10f));
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

        private static void MergeOpenPaths(List<List<Point2D>> openPaths, List<List<Point2D>> closedLoops)
        {
            var changed = true;

            while (changed)
            {
                changed = false;

                for (var i = 0; i < openPaths.Count; i++)
                {
                    var first = openPaths[i];
                    if (IsClosed(first))
                    {
                        closedLoops.Add(CloseLoop(first));
                        openPaths.RemoveAt(i);
                        changed = true;
                        break;
                    }

                    for (var j = i + 1; j < openPaths.Count; j++)
                    {
                        var second = openPaths[j];
                        if (!TryMerge(first, second, out var merged))
                        {
                            continue;
                        }

                        openPaths[i] = merged;
                        openPaths.RemoveAt(j);
                        changed = true;
                        break;
                    }

                    if (changed)
                    {
                        break;
                    }
                }
            }

            for (var i = openPaths.Count - 1; i >= 0; i--)
            {
                if (IsClosed(openPaths[i]))
                {
                    closedLoops.Add(CloseLoop(openPaths[i]));
                    openPaths.RemoveAt(i);
                }
            }
        }

        private static bool TryMerge(List<Point2D> a, List<Point2D> b, out List<Point2D> merged)
        {
            merged = new List<Point2D>();

            if (Distance(a[^1], b[0]) <= JoinTolerance)
            {
                merged = a.Concat(b.Skip(1)).ToList();
                return true;
            }

            if (Distance(a[^1], b[^1]) <= JoinTolerance)
            {
                merged = a.Concat(b.Take(b.Count - 1).Reverse()).ToList();
                return true;
            }

            if (Distance(a[0], b[^1]) <= JoinTolerance)
            {
                merged = b.Concat(a.Skip(1)).ToList();
                return true;
            }

            if (Distance(a[0], b[0]) <= JoinTolerance)
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
                if (result.Count == 0 || Distance(result[^1], point) > 0.001f)
                {
                    result.Add(point);
                }
            }

            return result;
        }

        private static bool IsClosed(List<Point2D> path)
        {
            return path.Count >= 3 && Distance(path[0], path[^1]) <= JoinTolerance;
        }

        private static List<Point2D> CloseLoop(List<Point2D> path)
        {
            var result = NormalizePath(path);
            if (result.Count == 0)
            {
                return result;
            }

            if (Distance(result[0], result[^1]) > JoinTolerance)
            {
                result.Add(result[0]);
            }
            else
            {
                result[^1] = result[0];
            }

            return result;
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
            public float Area { get; set; }
            public float AbsArea { get; set; }
            public Point2D Centroid { get; set; }
        }
    }
}