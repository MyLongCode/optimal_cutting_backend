
using SkiaSharp;
using vega.Controllers.DTO;
using vega.Models;
using vega.Services.Interfaces;

namespace vega.Services
{
    public class DrawService : IDrawService
    {
        public async Task<byte[]> DrawDXFAsync(List<FigureDTO> figures)
        {
            var maxX = figures.Max(f => float.Parse(f.Coorditanes.Split(';')[0]));
            var maxY = figures.Max(f => float.Parse(f.Coorditanes.Split(';')[1]));
            var minX = figures.Min(f => float.Parse(f.Coorditanes.Split(';')[0]));
            var minY = figures.Min(f => float.Parse(f.Coorditanes.Split(';')[1]));
            var width = (int)((maxX - minX) * 1.2);
            var height = (int)((maxY - minY) * 1.3);

            var bitmap = new SKBitmap(width, height);
            var canvas = new SKCanvas(bitmap);

            var blackPaint = new SKPaint();
            blackPaint.Color = SKColors.Black;

            var whitePaint = new SKPaint();
            whitePaint.Color = SKColors.White;
            whitePaint.Style = SKPaintStyle.Stroke;

            canvas.Clear(SKColors.Black);
            foreach (var figure in figures)
            {
                //line
                if (figure.TypeId == 1)
                {
                    var coorditanes = figure.Coorditanes.Split(';');
                    var startX = NormalizeXCoordinate(width, coorditanes[0]);
                    var startY = NormalizeYCoordinate(height, coorditanes[1]);
                    var endX = NormalizeXCoordinate(width, coorditanes[2]);
                    var endY = NormalizeYCoordinate(height, coorditanes[3]);
                    canvas.DrawLine(new SKPoint(startX, startY), new SKPoint(endX, endY), whitePaint);
                }
                //circle
                if (figure.TypeId == 2)
                {
                    var coorditanes = figure.Coorditanes.Split(';');
                    var centerX = NormalizeXCoordinate(width, coorditanes[0]);
                    var centerY = NormalizeYCoordinate(height, coorditanes[1]);
                    var radius = float.Parse(coorditanes[2]);
                    canvas.DrawCircle(centerX, centerY, radius, whitePaint);
                    canvas.DrawCircle(centerX, centerY, radius - 1, blackPaint);
                }
                //arc
                if (figure.TypeId == 3)
                {
                    var coorditanes = figure.Coorditanes.Split(';');
                    var centerX = NormalizeXCoordinate(width, coorditanes[0]);
                    var centerY = NormalizeYCoordinate(height, coorditanes[1]);
                    var radius = float.Parse(coorditanes[2]);
                    var startAngle = float.Parse(coorditanes[3]);
                    var endAngle = float.Parse(coorditanes[4]);
                    canvas.DrawArc(new SKRect(centerX - radius,
                        centerY - radius,
                        centerX + radius,
                        centerY + radius), startAngle, Math.Abs(endAngle + 360 - startAngle) % 360, false, whitePaint);
                }
            }
            var image = SKImage.FromBitmap(bitmap);
            return image.Encode().ToArray();
        }
        public async Task<byte[]> Draw1DCuttingAsync(Cutting1DResult result)
        {
            var width = result.Workpieces.Max(w => w.Details.Count()) * 100 + 10;
            var height = result.Workpieces.Count * 40 + 10;
            var detailHeight = height / result.Workpieces.Count;
            var detailWidthCoeff = (double)width / result.Workpieces.Max(w => w.Length);

            var bitmap = new SKBitmap(width, height);
            var canvas = new SKCanvas(bitmap);

            var colors = new List<SKColor> { new SKColor(175, 244, 158), new SKColor(128, 241, 205), new SKColor(124, 232, 237), new SKColor(158, 219, 244), new SKColor(152, 206, 255), new SKColor(180, 186, 255), new SKColor(227, 158, 244), new SKColor(255, 157, 203), new SKColor(255, 167, 172), new SKColor(244, 183, 158) };
            var detailColorsDict = new Dictionary<int, SKColor>();

            var detailPaint = new SKPaint();
            var grayPaint = new SKPaint();
            var blackPaint = new SKPaint();
            var textPaint = new SKPaint();
            blackPaint.Color = SKColors.Black;
            textPaint.Color = SKColors.Black;
            textPaint.TextSize = 14;
            grayPaint.Color = SKColors.LightGray;
            int x = 0;
            int y = 0;
            foreach (var workpiece in result.Workpieces)
            {
                foreach (var detailWidth in workpiece.Details)
                {
                    var key = detailWidth;
                    var rnd = new Random();
                    if (!detailColorsDict.ContainsKey(key)) detailColorsDict[key] = colors[rnd.Next(0, colors.Count)];
                    detailPaint.Color = detailColorsDict[key];
                    var newDetailWidth = (int)(detailWidth * detailWidthCoeff);
                    canvas.DrawRect(new SKRect(x, y, x + newDetailWidth, y + detailHeight), blackPaint);
                    canvas.DrawRect(new SKRect(x + 1, y + 1, x + newDetailWidth - 1, y + detailHeight - 1),
                        detailPaint);
                    canvas.DrawText(detailWidth.ToString(), x + 2, y + detailHeight / 2 + 10, textPaint);
                    x += newDetailWidth;
                }
                canvas.DrawRect(new SKRect(x, y, (int)(workpiece.Length * detailWidthCoeff), y + detailHeight), blackPaint);
                canvas.DrawRect(new SKRect(x + 1, y + 1, (int)(workpiece.Length * detailWidthCoeff) - 1, y + detailHeight - 1), grayPaint);
                canvas.DrawText((workpiece.Length - workpiece.Details.Sum(d => d)).ToString(), x + 2, y + detailHeight / 2 + 10, textPaint);
                x = 0;
                y += detailHeight;
            }
            var image = SKImage.FromBitmap(bitmap);
            return image.Encode().ToArray();

        }
        public async Task<List<byte[]>> Draw2DCuttingAsync(Cutting2DResult result)
        {
            var images = new List<byte[]>();

            var colors = new List<SKColor> { new SKColor(175, 244, 158), new SKColor(128, 241, 205), new SKColor(124, 232, 237), new SKColor(158, 219, 244), new SKColor(152, 206, 255), new SKColor(180, 186, 255), new SKColor(227, 158, 244), new SKColor(255, 157, 203), new SKColor(255, 167, 172), new SKColor(244, 183, 158) };
            var detailColorsDict = new Dictionary<int, SKColor>();


            var width = result.Workpiece.Width + 20;
            var height = result.Workpiece.Height + 10;

            var bitmap = new SKBitmap(width, height);
            var canvas = new SKCanvas(bitmap);
            canvas.Clear();

            var detailPaint = new SKPaint();
            var grayPaint = new SKPaint();
            var blackPaint = new SKPaint();
            var textPaint = new SKPaint();
            blackPaint.Color = SKColors.Black;
            textPaint.Color = SKColors.Black;
            textPaint.TextSize = 14;
            foreach (var workpiece in result.Details)
            {
                canvas.Clear(SKColors.White);
                foreach (var detail in workpiece)
                {
                    var key = detail.Width * detail.Height;
                    var rnd = new Random();
                    if (!detailColorsDict.ContainsKey(key)) detailColorsDict[key] = colors[rnd.Next(0, colors.Count)];
                    detailPaint.Color = detailColorsDict[key];
                    canvas.DrawRect(new SKRect(detail.X, detail.Y, detail.X + detail.Width, detail.Y + detail.Height), blackPaint);
                    canvas.DrawRect(new SKRect(detail.X + 1, detail.Y + 1, detail.X + detail.Width - 1, detail.Y + detail.Height - 1),
                        detailPaint);
                    textPaint.TextSize = (Math.Max(12, detail.Width/10));
                    if (detail.Width > 40 && detail.Height > 20)
                        canvas.DrawText($"{detail.Width}x{detail.Height}", detail.X + 2, detail.Y + textPaint.TextSize, textPaint);
                }
                images.Add(SKImage.FromBitmap(bitmap).Encode().ToArray());
            }
            
            return images;
        }

        private float NormalizeXCoordinate(int width, string x)
        {
            return width / 2 + float.Parse(x);
        }
        private float NormalizeYCoordinate(int height, string y)
        {
            return height / 2 + float.Parse(y);
        }

        
    }
}
