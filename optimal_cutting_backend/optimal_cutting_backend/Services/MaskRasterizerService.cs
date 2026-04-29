using SkiaSharp;
using System.Globalization;
using vega.Migrations.DAL;
using vega.Models;
using vega.Services.Interfaces;

namespace vega.Services
{
    [Obsolete("Legacy raster/mask placement; use polygon nesting services.")]
    public class MaskRasterizerService : IMaskRasterizerService
    {
        public void RasterizeDetail(Detail2D detail, float gridStep, float clearance)
        {
            detail.GridStep = gridStep <= 0 ? 1.0f : gridStep;

            if (detail.Figures != null && detail.Figures.Count > 0)
            {
                detail.Mask0 = RasterizeFromFigures(detail, rotated: false, detail.GridStep, clearance);
                detail.Mask90 = RasterizeFromFigures(detail, rotated: true, detail.GridStep, clearance);
            }
            else
            {
                if (detail.Contour == null)
                {
                    throw new InvalidOperationException("Detail contour is not prepared.");
                }

                detail.Mask0 = RasterizeContour(detail.Contour, detail.GridStep, clearance);
                detail.Mask90 = RasterizeContour(detail.RotatedContour ?? detail.Contour, detail.GridStep, clearance);
            }

            detail.ApproxArea = detail.Mask0.PartOccupiedCells * detail.GridStep * detail.GridStep;
            detail.Width = detail.OriginalWidth;
            detail.Height = detail.OriginalHeight;
        }

        private static DetailMask RasterizeFromFigures(Detail2D detail, bool rotated, float gridStep, float clearance)
        {
            detail.InitializeBounds();

            var points = GetAllGeometryPoints(detail, rotated).ToList();
            if (points.Count == 0)
            {
                throw new InvalidOperationException($"No geometry points found for detail '{detail.Name}'.");
            }

            var minX = points.Min(p => p.X);
            var minY = points.Min(p => p.Y);
            var maxX = points.Max(p => p.X);
            var maxY = points.Max(p => p.Y);

            var dilationRadiusCells = Math.Max(0, (int)Math.Ceiling(clearance / (2f * gridStep)));
            var paddingCells = Math.Max(3, dilationRadiusCells + 3);

            var widthCells = Math.Max(1, (int)Math.Ceiling((maxX - minX) / gridStep)) + paddingCells * 2 + 3;
            var heightCells = Math.Max(1, (int)Math.Ceiling((maxY - minY) / gridStep)) + paddingCells * 2 + 3;

            using var bitmap = new SKBitmap(widthCells, heightCells, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.Black);
            canvas.Translate(0, heightCells);
            canvas.Scale(1, -1);

            using var edgePaint = new SKPaint
            {
                Color = SKColors.White,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                IsAntialias = false,
                StrokeCap = SKStrokeCap.Square,
                StrokeJoin = SKStrokeJoin.Miter
            };

            foreach (var figure in detail.Figures)
            {
                DrawFigure(
                    canvas,
                    edgePaint,
                    figure,
                    rotated,
                    minX,
                    minY,
                    gridStep,
                    paddingCells);
            }

            var edgeMask = ReadBitmap(bitmap);
            var rawFilled = FillInside(edgeMask);
            var occupiedBeforeDilate = CountOccupied(rawFilled);
            var finalCells = dilationRadiusCells > 0 ? Dilate(rawFilled, dilationRadiusCells) : rawFilled;

            return new DetailMask
            {
                Cells = finalCells,
                WidthCells = finalCells.Length,
                HeightCells = finalCells.Length == 0 ? 0 : finalCells[0].Length,
                OriginOffsetXCells = paddingCells,
                OriginOffsetYCells = paddingCells,
                PartOccupiedCells = occupiedBeforeDilate,
                GridStep = gridStep
            };
        }

        private static void DrawFigure(
            SKCanvas canvas,
            SKPaint paint,
            Figure figure,
            bool rotated,
            float minX,
            float minY,
            float gridStep,
            int paddingCells)
        {
            switch (figure.TypeId)
            {
                case 1:
                    DrawLine(canvas, paint, figure, rotated, minX, minY, gridStep, paddingCells);
                    break;
                case 2:
                    DrawCircle(canvas, paint, figure, rotated, minX, minY, gridStep, paddingCells);
                    break;
                case 3:
                    DrawArc(canvas, paint, figure, rotated, minX, minY, gridStep, paddingCells);
                    break;
                case 4:
                    DrawSpline(canvas, paint, figure, rotated, minX, minY, gridStep, paddingCells);
                    break;
            }
        }

        private static void DrawLine(
            SKCanvas canvas,
            SKPaint paint,
            Figure figure,
            bool rotated,
            float minX,
            float minY,
            float gridStep,
            int paddingCells)
        {
            var coords = ParseCoords(figure.Coordinates);
            if (coords.Count < 4)
            {
                return;
            }

            var p1 = TransformPoint(coords[0], coords[1], rotated);
            var p2 = TransformPoint(coords[2], coords[3], rotated);

            canvas.DrawLine(
                ToCanvasPoint(p1.X, p1.Y, minX, minY, gridStep, paddingCells),
                ToCanvasPoint(p2.X, p2.Y, minX, minY, gridStep, paddingCells),
                paint);
        }

        private static void DrawCircle(
            SKCanvas canvas,
            SKPaint paint,
            Figure figure,
            bool rotated,
            float minX,
            float minY,
            float gridStep,
            int paddingCells)
        {
            var coords = ParseCoords(figure.Coordinates);
            if (coords.Count < 3)
            {
                return;
            }

            var center = TransformPoint(coords[0], coords[1], rotated);
            var radius = coords[2] / gridStep;

            var c = ToCanvasPoint(center.X, center.Y, minX, minY, gridStep, paddingCells);
            canvas.DrawCircle(c.X, c.Y, radius, paint);
        }

        private static void DrawArc(
            SKCanvas canvas,
            SKPaint paint,
            Figure figure,
            bool rotated,
            float minX,
            float minY,
            float gridStep,
            int paddingCells)
        {
            var coords = ParseCoords(figure.Coordinates);
            if (coords.Count < 5)
            {
                return;
            }

            var center = TransformPoint(coords[0], coords[1], rotated);
            var radius = coords[2] / gridStep;
            var startAngle = NormalizeAngle(coords[3] + (rotated ? 90f : 0f));
            var endAngle = NormalizeAngle(coords[4] + (rotated ? 90f : 0f));

            var sweep = endAngle - startAngle;
            if (sweep <= 0)
            {
                sweep += 360f;
            }

            var c = ToCanvasPoint(center.X, center.Y, minX, minY, gridStep, paddingCells);
            var rect = new SKRect(c.X - radius, c.Y - radius, c.X + radius, c.Y + radius);
            canvas.DrawArc(rect, startAngle, sweep, false, paint);
        }

        private static void DrawSpline(
            SKCanvas canvas,
            SKPaint paint,
            Figure figure,
            bool rotated,
            float minX,
            float minY,
            float gridStep,
            int paddingCells)
        {
            var culture = new CultureInfo("ru-RU");
            var points = figure.Coordinates.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (points.Length < 2)
            {
                return;
            }

            var transformed = new List<SKPoint>();
            foreach (var point in points)
            {
                var parts = point.Replace('.', ',').Split(';', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                var x = float.Parse(parts[0], culture);
                var y = float.Parse(parts[1], culture);
                var t = TransformPoint(x, y, rotated);
                transformed.Add(ToCanvasPoint(t.X, t.Y, minX, minY, gridStep, paddingCells));
            }

            for (var i = 0; i < transformed.Count - 1; i++)
            {
                canvas.DrawLine(transformed[i], transformed[i + 1], paint);
            }
        }

        private static IEnumerable<Point2D> GetAllGeometryPoints(Detail2D detail, bool rotated)
        {
            foreach (var figure in detail.Figures)
            {
                switch (figure.TypeId)
                {
                    case 1:
                        {
                            var coords = ParseCoords(figure.Coordinates);
                            if (coords.Count >= 4)
                            {
                                yield return TransformPoint(coords[0], coords[1], rotated);
                                yield return TransformPoint(coords[2], coords[3], rotated);
                            }
                            break;
                        }
                    case 2:
                        {
                            var coords = ParseCoords(figure.Coordinates);
                            if (coords.Count >= 3)
                            {
                                var c = TransformPoint(coords[0], coords[1], rotated);
                                var r = coords[2];
                                yield return new Point2D(c.X - r, c.Y - r);
                                yield return new Point2D(c.X + r, c.Y + r);
                            }
                            break;
                        }
                    case 3:
                        {
                            var coords = ParseCoords(figure.Coordinates);
                            if (coords.Count >= 5)
                            {
                                var cx = coords[0];
                                var cy = coords[1];
                                var r = coords[2];
                                var start = coords[3];
                                var end = coords[4];

                                var sweep = NormalizeAngle(end) - NormalizeAngle(start);
                                if (sweep <= 0)
                                {
                                    sweep += 360f;
                                }

                                var segments = Math.Max(16, (int)Math.Ceiling(sweep / 4f));
                                for (var i = 0; i <= segments; i++)
                                {
                                    var angleDeg = NormalizeAngle(start + sweep * i / segments + (rotated ? 90f : 0f));
                                    var angleRad = angleDeg * Math.PI / 180.0;
                                    var x = (float)(cx + r * Math.Cos(angleRad));
                                    var y = (float)(cy + r * Math.Sin(angleRad));
                                    yield return TransformPoint(x, y, rotated: false);
                                }
                            }
                            break;
                        }
                    case 4:
                        {
                            var culture = new CultureInfo("ru-RU");
                            var points = figure.Coordinates.Split('/', StringSplitOptions.RemoveEmptyEntries);
                            foreach (var point in points)
                            {
                                var parts = point.Replace('.', ',').Split(';', StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length < 2)
                                {
                                    continue;
                                }

                                var x = float.Parse(parts[0], culture);
                                var y = float.Parse(parts[1], culture);
                                yield return TransformPoint(x, y, rotated);
                            }
                            break;
                        }
                }
            }
        }

        private static Point2D TransformPoint(float x, float y, bool rotated)
        {
            return rotated ? new Point2D(-y, x) : new Point2D(x, y);
        }

        private static SKPoint ToCanvasPoint(
            float x,
            float y,
            float minX,
            float minY,
            float gridStep,
            int paddingCells)
        {
            return new SKPoint(
                paddingCells + (x - minX) / gridStep,
                paddingCells + (y - minY) / gridStep);
        }

        private static byte[][] ReadBitmap(SKBitmap bitmap)
        {
            var result = CreateArray(bitmap.Width, bitmap.Height);

            for (var x = 0; x < bitmap.Width; x++)
            {
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var pixel = bitmap.GetPixel(x, bitmap.Height - 1 - y);
                    result[x][y] = pixel.Red > 0 || pixel.Green > 0 || pixel.Blue > 0 || pixel.Alpha > 0 ? (byte)1 : (byte)0;
                }
            }

            return result;
        }

        private static byte[][] FillInside(byte[][] edges)
        {
            var width = edges.Length;
            var height = width == 0 ? 0 : edges[0].Length;

            var outside = CreateArray(width, height);
            var queue = new Queue<(int x, int y)>();

            void TryEnqueue(int x, int y)
            {
                if (x < 0 || y < 0 || x >= width || y >= height)
                {
                    return;
                }

                if (outside[x][y] > 0 || edges[x][y] > 0)
                {
                    return;
                }

                outside[x][y] = 1;
                queue.Enqueue((x, y));
            }

            for (var x = 0; x < width; x++)
            {
                TryEnqueue(x, 0);
                TryEnqueue(x, height - 1);
            }

            for (var y = 0; y < height; y++)
            {
                TryEnqueue(0, y);
                TryEnqueue(width - 1, y);
            }

            var directions = new (int dx, int dy)[]
            {
                (1, 0), (-1, 0), (0, 1), (0, -1)
            };

            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();
                foreach (var (dx, dy) in directions)
                {
                    TryEnqueue(cx + dx, cy + dy);
                }
            }

            var filled = CreateArray(width, height);
            for (var x = 0; x < width; x++)
            {
                for (var y = 0; y < height; y++)
                {
                    if (outside[x][y] == 0)
                    {
                        filled[x][y] = 1;
                    }
                }
            }

            return filled;
        }

        private static int CountOccupied(byte[][] cells)
        {
            var count = 0;
            for (var x = 0; x < cells.Length; x++)
            {
                for (var y = 0; y < cells[x].Length; y++)
                {
                    if (cells[x][y] > 0)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static byte[][] Dilate(byte[][] source, int radius)
        {
            var width = source.Length;
            var height = width == 0 ? 0 : source[0].Length;

            var target = CreateArray(width, height);
            var offsets = new List<(int dx, int dy)>();

            for (var dx = -radius; dx <= radius; dx++)
            {
                for (var dy = -radius; dy <= radius; dy++)
                {
                    if (dx * dx + dy * dy <= radius * radius)
                    {
                        offsets.Add((dx, dy));
                    }
                }
            }

            for (var x = 0; x < width; x++)
            {
                for (var y = 0; y < height; y++)
                {
                    if (source[x][y] == 0)
                    {
                        continue;
                    }

                    foreach (var (dx, dy) in offsets)
                    {
                        var nx = x + dx;
                        var ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < width && ny < height)
                        {
                            target[nx][ny] = 1;
                        }
                    }
                }
            }

            return target;
        }

        private static byte[][] CreateArray(int width, int height)
        {
            var result = new byte[width][];
            for (var x = 0; x < width; x++)
            {
                result[x] = new byte[height];
            }

            return result;
        }

        private static List<float> ParseCoords(string coordinates)
        {
            return coordinates
                .Replace('.', ',')
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => float.Parse(x, new CultureInfo("ru-RU")))
                .ToList();
        }

        private static float NormalizeAngle(float angle)
        {
            return (angle % 360 + 360) % 360;
        }

        private static DetailMask RasterizeContour(Contour2D contour, float gridStep, float clearance)
        {
            var dilationRadiusCells = Math.Max(0, (int)Math.Ceiling(clearance / (2f * gridStep)));
            var paddingCells = Math.Max(1, dilationRadiusCells + 1);

            var widthCells = Math.Max(1, (int)Math.Ceiling(contour.GetWidth() / gridStep)) + paddingCells * 2 + 1;
            var heightCells = Math.Max(1, (int)Math.Ceiling(contour.GetHeight() / gridStep)) + paddingCells * 2 + 1;

            using var bitmap = new SKBitmap(widthCells, heightCells, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.Black);

            using var fillPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = SKColors.White,
                IsAntialias = false
            };

            foreach (var solid in contour.FilledContours)
            {
                using var path = BuildPath(solid, gridStep, paddingCells);
                canvas.DrawPath(path, fillPaint);
            }

            fillPaint.Color = SKColors.Black;
            foreach (var hole in contour.HoleContours)
            {
                using var path = BuildPath(hole, gridStep, paddingCells);
                canvas.DrawPath(path, fillPaint);
            }

            var rawCells = ReadMask(bitmap);
            var rawOccupied = CountOccupied(rawCells);
            var finalCells = dilationRadiusCells > 0 ? Dilate(rawCells, dilationRadiusCells) : rawCells;

            return new DetailMask
            {
                Cells = finalCells,
                WidthCells = finalCells.Length,
                HeightCells = finalCells.Length == 0 ? 0 : finalCells[0].Length,
                OriginOffsetXCells = paddingCells,
                OriginOffsetYCells = paddingCells,
                PartOccupiedCells = rawOccupied,
                GridStep = gridStep
            };
        }

        private static SKPath BuildPath(List<Point2D> loop, float gridStep, int paddingCells)
        {
            var path = new SKPath();
            if (loop.Count == 0)
            {
                return path;
            }

            path.MoveTo(loop[0].X / gridStep + paddingCells, loop[0].Y / gridStep + paddingCells);
            for (var i = 1; i < loop.Count; i++)
            {
                path.LineTo(loop[i].X / gridStep + paddingCells, loop[i].Y / gridStep + paddingCells);
            }

            path.Close();
            return path;
        }

        private static byte[][] ReadMask(SKBitmap bitmap)
        {
            var result = new byte[bitmap.Width][];
            for (var x = 0; x < bitmap.Width; x++)
            {
                result[x] = new byte[bitmap.Height];
            }

            for (var x = 0; x < bitmap.Width; x++)
            {
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var pixel = bitmap.GetPixel(x, bitmap.Height - 1 - y);
                    result[x][y] = pixel.Red > 0 || pixel.Alpha > 0 ? (byte)1 : (byte)0;
                }
            }

            return result;
        }
    }
}