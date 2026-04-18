using SkiaSharp;
using vega.Models;
using vega.Services.Interfaces;

namespace vega.Services
{
    public class MaskRasterizerService : IMaskRasterizerService
    {
        public void RasterizeDetail(Detail2D detail, float gridStep, float clearance)
        {
            if (detail.Contour == null)
            {
                throw new InvalidOperationException("Detail contour is not prepared.");
            }

            detail.GridStep = gridStep <= 0 ? 1.0f : gridStep;

            detail.Mask0 = RasterizeContour(detail.Contour, detail.GridStep, clearance);
            detail.Mask90 = RasterizeContour(detail.RotatedContour ?? detail.Contour, detail.GridStep, clearance);

            detail.ApproxArea = detail.Mask0.PartOccupiedCells * detail.GridStep * detail.GridStep;
            detail.OriginalWidth = Math.Max(1, (int)Math.Ceiling(detail.Contour.GetWidth()));
            detail.OriginalHeight = Math.Max(1, (int)Math.Ceiling(detail.Contour.GetHeight()));
            detail.RotatedWidth = Math.Max(1, (int)Math.Ceiling((detail.RotatedContour ?? detail.Contour).GetWidth()));
            detail.RotatedHeight = Math.Max(1, (int)Math.Ceiling((detail.RotatedContour ?? detail.Contour).GetHeight()));
            detail.Width = detail.OriginalWidth;
            detail.Height = detail.OriginalHeight;
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

            var target = new byte[width][];
            for (var x = 0; x < width; x++)
            {
                target[x] = new byte[height];
            }

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
    }
}