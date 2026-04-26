
using netDxf;
using netDxf.Entities;
using netDxf.Header;
using netDxf.IO;
using SkiaSharp;
using System.Text;
using vega.Controllers.DTO;
using vega.Migrations.DAL;
using vega.Models;
using vega.Services.Interfaces;

namespace vega.Services
{
    public class DXFService : IDXFService
    {
        public async Task<List<byte[]>> Create2DDXFAsync(Cutting2DResult result)
        {
            var answer = new List<byte[]>();
            foreach (var item in result.Workpieces)
            {
                var dxf = new DxfDocument();
                foreach (var detail in item.Details)
                {
                    dxf.Entities.Add(new Line(new Vector2(detail.X, detail.Y), new Vector2(detail.X + detail.Width, detail.Y)));
                    dxf.Entities.Add(new Line(new Vector2(detail.X, detail.Y), new Vector2(detail.X, detail.Y + detail.Height)));
                    dxf.Entities.Add(new Line(new Vector2(detail.X + detail.Width, detail.Y), new Vector2(detail.X + detail.Width, detail.Y + detail.Height)));
                    dxf.Entities.Add(new Line(new Vector2(detail.X, detail.Y + detail.Height), new Vector2(detail.X + detail.Width, detail.Y + detail.Height)));
                }
                using (MemoryStream stream = new MemoryStream())
                {
                    dxf.Save(stream);
                    byte[] dxfBytes = stream.ToArray();
                    answer.Add(dxfBytes);
                }
            }
            return answer;

        }

        public async Task<List<byte[]>> CreateDXFAsync(Cutting2DResult result)
        {
            var answer = new List<byte[]>();
            foreach (var item in result.Workpieces)
            {
                var dxf = new DxfDocument();
                foreach (var detail in item.Details)
                {
                    foreach (var figure in detail.Figures)
                    {
                        var coorditanes = figure.Coordinates.Split(';').Select(f => float.Parse(f)).ToList();
                        if (figure.TypeId == 1)
                        {
                            var start = new Vector2(coorditanes[0], coorditanes[1]);
                            var finish = new Vector2(coorditanes[2], coorditanes[3]);
                            if (detail.Rotated)
                            {
                                (start.X, start.Y) = (-start.Y, start.X);
                                (finish.X, finish.Y) = (-finish.Y, finish.X);
                            }
                            var center = GetDetailCenter(detail.Figures, detail.X, detail.Y);
                            start += center;
                            finish += center;
                            dxf.Entities.Add(new Line(start, finish));
                        }

                    }
                }
                using (MemoryStream stream = new MemoryStream())
                {
                    dxf.Save(stream);
                    byte[] dxfBytes = stream.ToArray();
                    answer.Add(dxfBytes);
                }
            }
            return answer;
        }

        public async Task<List<Figure>> GetDXFAsync(byte[] fileBytes)
        {
            var ans = new List<Figure>();
            using var stream = new MemoryStream(fileBytes);
            var dxf = DxfDocument.Load(stream);
            foreach (var obj in dxf.Entities.All)
            {
                if (obj is Line line)
                    ans.Add(new Figure { TypeId = 1, Coordinates = $"{line.StartPoint.X}; {line.StartPoint.Y}; {line.EndPoint.X}; {line.EndPoint.Y}" });
                if (obj is Circle circle)
                    ans.Add(new Figure { TypeId = 2, Coordinates = $"{circle.Center.X}; {circle.Center.Y}; {circle.Radius}" });
                if (obj is Arc arc)
                    ans.Add(new Figure { TypeId = 3, Coordinates = $"{arc.Center.X}; {arc.Center.Y}; {arc.Radius}; {arc.StartAngle}; {arc.EndAngle}" });
                if (obj is Spline spline)
                {
                    var coordinates = new StringBuilder();
                    foreach (var item in spline.ControlPoints)
                        coordinates.Append($"{item.ToString()}/");
                    ans.Add(new Figure { TypeId = 4, Coordinates = coordinates.ToString() });
                }

            }
            return ans;
        }
        private Vector2 GetDetailCenter(List<Figure> figures, float detailX = 0, float detailY = 0)
        {
            var maxX = figures.Max(f => float.Parse(f.Coordinates.Split(';')[0]));
            var maxY = figures.Max(f => float.Parse(f.Coordinates.Split(';')[1]));
            var minX = figures.Min(f => float.Parse(f.Coordinates.Split(';')[0]));
            var minY = figures.Min(f => float.Parse(f.Coordinates.Split(';')[1]));

            var detailCenterX = (int)(((minX + maxX) / 2) + detailX);
            var detailCenterY = (int)(((minY + maxY) / 2) + detailY);

            return new Vector2(detailCenterX, detailCenterY);
        }
    }
}
