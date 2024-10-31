
using vega.Migrations.DAL;
using vega.Models;
using vega.Services.Interfaces;

namespace vega.Services
{
    public class Cutting2DService : ICutting2DService
    {
        public async Task<Cutting2DResult> CalculateCuttingAsync(List<Detail2D> details, Workpiece workpiece)
        {
            if (details.Max(d => d.Width) > workpiece.Width || details.Max(d => d.Height) > workpiece.Height) throw new Exception("detail > workpiece");
            details = details.OrderByDescending(d => d.Height).ToList();

            var workpieces = new List<List<Detail2D>>();
            while(details.Count > 0)
                workpieces.Add(CalculateCuttingForWorkpiece(details,workpiece));
            return new Cutting2DResult() { Details = workpieces, Workpiece = workpiece };
        }

        public List<Detail2D> CalculateCuttingForWorkpiece(List<Detail2D> details, Workpiece workpiece)
        {
            var result = new List<Detail2D>();
            int currentX = 0;
            int currentY = 0;
            int maxHeightInRow = 0;
            int j = 0;
            while (details.Count > 0)
            {
                var detail = details[j];
                if (currentX + detail.Width > workpiece.Width)
                {
                    currentX = 0;
                    currentY += maxHeightInRow;
                    maxHeightInRow = 0;
                }

                if (currentY + detail.Height > workpiece.Height)
                {
                    return result;
                }

                currentX += detail.Width;
                maxHeightInRow = Math.Max(maxHeightInRow, detail.Height);
                detail.X = currentX - detail.Width;
                detail.Y = currentY;
                result.Add(detail);
                details.Remove(detail);
            }
            return result;
        }
    }
}
