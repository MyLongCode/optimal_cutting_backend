using vega.Migrations.DAL;
using vega.Models;
using vega.Services.Interfaces;

namespace vega.Services
{
    public class MaskPlacementService : IMaskPlacementService
    {
        public Workpiece2D PlaceSingleWorkpiece(
            List<Detail2D> remainingDetails,
            Workpiece workpiece,
            int indent,
            float gridStep,
            bool allowRotate90)
        {
            var usableWidth = workpiece.Width - 2 * indent;
            var usableHeight = workpiece.Height - 2 * indent;

            if (usableWidth <= 0 || usableHeight <= 0)
            {
                throw new Exception("workpiece size is too small");
            }

            var widthCells = Math.Max(1, (int)Math.Floor(usableWidth / gridStep));
            var heightCells = Math.Max(1, (int)Math.Floor(usableHeight / gridStep));

            var sheet = new SheetMask
            {
                GridStep = gridStep,
                Indent = indent,
                WidthCells = widthCells,
                HeightCells = heightCells,
                Occupancy = CreateArray(widthCells, heightCells)
            };

            var placed = new List<Detail2D>();
            double usedArea = 0;
            var placedSomething = true;

            while (placedSomething && remainingDetails.Count > 0)
            {
                placedSomething = false;

                var ordered = remainingDetails
                    .OrderByDescending(d => d.ApproxArea)
                    .ThenByDescending(d => Math.Max(d.Mask0?.WidthCells ?? 0, d.Mask90?.WidthCells ?? 0))
                    .ToList();

                foreach (var detail in ordered)
                {
                    if (!TryPlaceDetail(sheet, detail, indent, allowRotate90, out var placedDetail, out var usedCells))
                    {
                        continue;
                    }

                    placed.Add(placedDetail);
                    usedArea += usedCells * gridStep * gridStep;
                    remainingDetails.Remove(detail);
                    placedSomething = true;
                }
            }

            return new Workpiece2D
            {
                Width = workpiece.Width,
                Height = workpiece.Height,
                Details = placed,
                ProcentUsage = Math.Round(usedArea / (usableWidth * (double)usableHeight), 2)
            };
        }

        private static bool TryPlaceDetail(
            SheetMask sheet,
            Detail2D detail,
            int indent,
            bool allowRotate90,
            out Detail2D placedDetail,
            out int usedCells)
        {
            placedDetail = null!;
            usedCells = 0;

            var rotations = allowRotate90
                ? new[] { false, true }
                : new[] { false };

            foreach (var rotated in rotations)
            {
                var mask = rotated ? detail.Mask90 : detail.Mask0;
                if (mask == null)
                {
                    continue;
                }

                if (mask.WidthCells > sheet.WidthCells || mask.HeightCells > sheet.HeightCells)
                {
                    continue;
                }

                for (var y = 0; y <= sheet.HeightCells - mask.HeightCells; y++)
                {
                    for (var x = 0; x <= sheet.WidthCells - mask.WidthCells; x++)
                    {
                        if (!CanPlace(sheet.Occupancy, mask.Cells, x, y))
                        {
                            continue;
                        }

                        Blit(sheet.Occupancy, mask.Cells, x, y);

                        var clone = detail.CloneForPlacement();
                        clone.ApplyPlacement(
                            rotated,
                            indent + (x + mask.OriginOffsetXCells) * sheet.GridStep,
                            indent + (y + mask.OriginOffsetYCells) * sheet.GridStep
                        );

                        placedDetail = clone;
                        usedCells = mask.PartOccupiedCells;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool CanPlace(byte[][] sheet, byte[][] mask, int startX, int startY)
        {
            for (var x = 0; x < mask.Length; x++)
            {
                for (var y = 0; y < mask[x].Length; y++)
                {
                    if (mask[x][y] == 0)
                    {
                        continue;
                    }

                    if (sheet[startX + x][startY + y] > 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void Blit(byte[][] sheet, byte[][] mask, int startX, int startY)
        {
            for (var x = 0; x < mask.Length; x++)
            {
                for (var y = 0; y < mask[x].Length; y++)
                {
                    if (mask[x][y] > 0)
                    {
                        sheet[startX + x][startY + y] = 1;
                    }
                }
            }
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
    }
}