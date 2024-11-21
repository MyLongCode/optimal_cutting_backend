
using Org.BouncyCastle.Crypto.Prng;
using vega.Migrations.DAL;
using vega.Models;
using vega.Services.Interfaces;

namespace vega.Services
{
    public class Cutting2DService : ICutting2DService
    {
        public int Indent = 10;
        public async Task<Cutting2DResult> CalculateCuttingAsync(List<Detail2D> details, Workpiece workpiece)
        {
            if (details.Max(d => d.Width) > workpiece.Width || details.Max(d => d.Height) > workpiece.Height) throw new Exception("detail > workpiece");
            details = details.OrderByDescending(d => d.Height).ToList();
            //отступ от краёв
            var indent = 10;
            var workpieces = new List<List<Detail2D>>();
            while(details.Count > 0)
                workpieces.Add(CalculateCuttingForWorkpiece(details, workpiece));
                
            return new Cutting2DResult() { Details = workpieces, Workpiece = workpiece };
        }

        //public List<Detail2D> CalculateCuttingForWorkpiece(List<Detail2D> details, Workpiece workpiece)
        //{
        //    var arr = new List<Detail2D>();
        //    var currentX = 0;
        //    var currentY = 0;
        //    var maxHeightInRow = 0;

        //    var lastX = 0;
        //    var lastDetailHeight = 0;
        //    var highInRow = 0;

        //    var i = 0;
        //    while (details.Count > 0)
        //    {
        //        var detail = details[i];
        //        var j = 0;
        //        highInRow = 0;
        //        while (details.Count > 0)
        //        {
        //            detail = details[j];
        //            if (j < details.Count)
        //                if (detail.Width + currentX <= workpiece.Width && detail.Height + currentY <= workpiece.Height)
        //                {
        //                    detail.X = currentX;
        //                    detail.Y = currentY;
        //                    arr.Add(detail);
        //                    details.RemoveAt(j);
        //                    j--;
        //                    currentX += detail.Width;
        //                    lastDetailHeight = detail.Height;
        //                    highInRow = Math.Max(highInRow, detail.Height);
        //                }
        //            else break;
        //            j++;
        //        }
                
        //        if (currentY + detail.Height > workpiece.Height) return arr;
        //        currentY += highInRow;
        //        currentX = 0;
        //    }
        //    return arr;
        //}

        public List<Detail2D> CalculateCuttingForWorkpiece(List<Detail2D> details, Workpiece workpiece)
        {
            var result = new List<Detail2D>();
            workpiece.Width -= 2 * Indent;
            workpiece.Height -= 2 * Indent;
            var arr = new byte[workpiece.Width][];
            arr = arr.Select(x => new byte[workpiece.Height]).ToArray();
            int currX = 0, currY = 0;


            while(details.Count > 0)
            {
                var i = 0;
                currX = CanAddToRow(arr, currY);
                if (currX != -1)
                {
                    arr[currX][currY] = 1;
                }
                else currY++;
                if (currY >= workpiece.Height) break;
                while (i < details.Count)
                {
                    var detail = details[i];
                    currX = CanAddToRow(arr, currY);
                    if (currX == -1) break;
                    if (CanAddDetail(arr, detail, currX, currY))
                    {
                        AddDetail(arr, detail, currX, currY);
                        detail.X = currX;
                        detail.Y = currY;
                        result.Add(detail);
                        details.RemoveAt(i);
                        i--;
                    }
                    else
                    {
                        detail = RotateDetail(detail);
                        if (CanAddDetail(arr, detail, currX, currY))
                        {
                            AddDetail(arr, detail, currX, currY);
                            detail.X = currX;
                            detail.Y = currY;
                            result.Add(detail);
                            details.RemoveAt(i);
                            i--;
                        }
                    }
                    i++;
                }
            }
            result = result.Select(x =>
            {
                x.X += 10;
                x.Y += 10;
                return x;
            }).ToList();
            workpiece.Width += 2 * Indent;
            workpiece.Height += 2 * Indent;
            return result;
        }

        public byte[][] AddDetail(byte[][] arr, Detail2D detail, int x, int y)
        {
            for (int i = 0; i < detail.Width; i++) 
                for(int j = 0; j < detail.Height; j++)
                    arr[x + i][y + j]++;
            return arr;
        }

        public Detail2D RotateDetail(Detail2D detail)
        {
            var width = detail.Width;
            detail.Width = detail.Height;
            detail.Height = width;
            return detail;
        }

        public bool CanAddDetail(byte[][] arr, Detail2D detail, int x, int y)
        {
            if (x < 0 || y < 0) return false;
            if (x + detail.Width >= arr.Length) return false;
            if (y + detail.Height >= arr[0].Length) return false;
            for (int i = 0; i < detail.Width; i++)
                for (int j = 0; j < detail.Height; j++)
                    if (arr[x + i][y + j] == 1) return false;
            return true;
        }

        public int CanAddToRow(byte[][] arr, int y)
        {
            for (int i = 0; i < arr.Length; i++)
                if (arr[i][y] == 0) return i;
            return -1;
        }
    }
}
