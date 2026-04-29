using System.Globalization;
using vega.Migrations.DAL;

namespace vega.Models
{
    public class Detail2D
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }
        public int RotatedWidth { get; set; }
        public int RotatedHeight { get; set; }

        public float X { get; set; }
        public float Y { get; set; }
        public string? Name { get; set; }

        public List<Figure> Figures { get; set; } = new();
        public bool Rotated { get; set; } = false;

        public float MinX { get; set; }
        public float MinY { get; set; }
        public float MaxX { get; set; }
        public float MaxY { get; set; }

        public Contour2D? Contour { get; set; }
        public Contour2D? RotatedContour { get; set; }

        public DetailMask? Mask0 { get; set; }
        public DetailMask? Mask90 { get; set; }

        public float GridStep { get; set; } = 1.0f;
        public double ApproxArea { get; set; }

        public Detail2D()
        {
        }

        public Detail2D(List<Figure> figures, string? name = "")
        {
            Figures = figures ?? new List<Figure>();
            Name = name;
            InitializeBounds();
        }

        public void InitializeBounds()
        {
            if (Figures == null || Figures.Count == 0)
            {
                MinX = 0;
                MinY = 0;
                MaxX = Width;
                MaxY = Height;
                OriginalWidth = Width;
                OriginalHeight = Height;
                RotatedWidth = Height;
                RotatedHeight = Width;
                return;
            }

            var maxX = float.MinValue;
            var minX = float.MaxValue;
            var maxY = float.MinValue;
            var minY = float.MaxValue;

            foreach (var figure in Figures)
            {
                var coords = ParseFigureCoordinates(figure);
                if (coords.Count == 0)
                {
                    continue;
                }

                if (figure.TypeId == 1)
                {
                    maxX = Math.Max(maxX, Math.Max(coords[0], coords[2]));
                    minX = Math.Min(minX, Math.Min(coords[0], coords[2]));
                    maxY = Math.Max(maxY, Math.Max(coords[1], coords[3]));
                    minY = Math.Min(minY, Math.Min(coords[1], coords[3]));
                }
                else if (figure.TypeId == 2)
                {
                    var cx = coords[0];
                    var cy = coords[1];
                    var r = coords[2];
                    maxX = Math.Max(maxX, cx + r);
                    minX = Math.Min(minX, cx - r);
                    maxY = Math.Max(maxY, cy + r);
                    minY = Math.Min(minY, cy - r);
                }
                else if (figure.TypeId == 3)
                {
                    var cx = coords[0];
                    var cy = coords[1];
                    var r = coords[2];
                    var start = coords[3];
                    var end = coords[4];

                    foreach (var point in GetArcExtremePoints(cx, cy, r, start, end))
                    {
                        maxX = Math.Max(maxX, point.x);
                        minX = Math.Min(minX, point.x);
                        maxY = Math.Max(maxY, point.y);
                        minY = Math.Min(minY, point.y);
                    }
                }
                else if (figure.TypeId == 4)
                {
                    var points = figure.Coordinates.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var point in points)
                    {
                        var parts = point.Replace('.', ',').Split(';', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2)
                        {
                            continue;
                        }

                        var px = float.Parse(parts[0], new CultureInfo("ru-RU"));
                        var py = float.Parse(parts[1], new CultureInfo("ru-RU"));
                        maxX = Math.Max(maxX, px);
                        minX = Math.Min(minX, px);
                        maxY = Math.Max(maxY, py);
                        minY = Math.Min(minY, py);
                    }
                }
            }

            if (minX == float.MaxValue || minY == float.MaxValue)
            {
                minX = 0;
                minY = 0;
                maxX = Width;
                maxY = Height;
            }

            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;

            OriginalWidth = Math.Max(1, (int)Math.Ceiling(MaxX - MinX));
            OriginalHeight = Math.Max(1, (int)Math.Ceiling(MaxY - MinY));
            RotatedWidth = OriginalHeight;
            RotatedHeight = OriginalWidth;

            Width = OriginalWidth;
            Height = OriginalHeight;
        }

        public void ApplyPlacement(bool rotated, float x, float y)
        {
            Rotated = rotated;
            X = x;
            Y = y;
            Width = rotated ? RotatedWidth : OriginalWidth;
            Height = rotated ? RotatedHeight : OriginalHeight;
        }

        public Detail2D CloneForPlacement()
        {
            return new Detail2D
            {
                Width = Width,
                Height = Height,
                OriginalWidth = OriginalWidth,
                OriginalHeight = OriginalHeight,
                RotatedWidth = RotatedWidth,
                RotatedHeight = RotatedHeight,
                X = X,
                Y = Y,
                Name = Name,
                Figures = Figures,
                Rotated = Rotated,
                MinX = MinX,
                MinY = MinY,
                MaxX = MaxX,
                MaxY = MaxY,
                Contour = Contour,
                RotatedContour = RotatedContour,
                Mask0 = Mask0,
                Mask90 = Mask90,
                GridStep = GridStep,
                ApproxArea = ApproxArea
            };
        }

        private static List<float> ParseFigureCoordinates(Figure figure)
        {
            return figure.Coordinates
                .Replace('.', ',')
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => float.Parse(x, new CultureInfo("ru-RU")))
                .ToList();
        }

        private static List<(float x, float y)> GetArcExtremePoints(float cx, float cy, float r, float startAngle, float endAngle)
        {
            List<(float x, float y)> points = new();

            static float Normalize(float angle) => (angle % 360 + 360) % 360;

            static bool IsAngleBetween(float angle, float start, float end)
            {
                angle = Normalize(angle);
                start = Normalize(start);
                end = Normalize(end);

                if (start < end)
                {
                    return angle >= start && angle <= end;
                }

                return angle >= start || angle <= end;
            }

            points.Add(PolarToCartesian(cx, cy, r, startAngle));
            points.Add(PolarToCartesian(cx, cy, r, endAngle));

            foreach (var a in new[] { 0f, 90f, 180f, 270f })
            {
                if (IsAngleBetween(a, startAngle, endAngle))
                {
                    points.Add(PolarToCartesian(cx, cy, r, a));
                }
            }

            return points;
        }

        private static (float x, float y) PolarToCartesian(float cx, float cy, float r, float angleDeg)
        {
            double rad = angleDeg * Math.PI / 180.0;
            return ((float)(cx + r * Math.Cos(rad)), (float)(cy + r * Math.Sin(rad)));
        }
    }
}