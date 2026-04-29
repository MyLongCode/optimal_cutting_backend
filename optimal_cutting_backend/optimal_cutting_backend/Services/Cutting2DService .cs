using vega.Migrations.DAL;
using vega.Models;
using vega.Services.Interfaces;

namespace vega.Services
{
    public class Cutting2DService : ICutting2DService
    {
        private readonly IContourBuilderService _contourBuilderService;
        private readonly IMaskRasterizerService _maskRasterizerService;
        private readonly IMaskPlacementService _maskPlacementService;

        public Cutting2DService(
            IContourBuilderService contourBuilderService,
            IMaskRasterizerService maskRasterizerService,
            IMaskPlacementService maskPlacementService)
        {
            _contourBuilderService = contourBuilderService;
            _maskRasterizerService = maskRasterizerService;
            _maskPlacementService = maskPlacementService;
        }

        public async Task<Cutting2DResult> CalculateCuttingAsync(
            List<Detail2D> details,
            Workpiece workpiece,
            float thickness,
            int indent,
            float gridStep = 1.0f,
            bool allowRotate90 = true,
            bool useMaskNesting = true)
        {
            if (details == null || details.Count == 0)
            {
                return await Task.FromResult(new Cutting2DResult
                {
                    Workpieces = new List<Workpiece2D>(),
                    TotalPercentUsage = 0,
                    Algorithm = "mask",
                    GridStep = gridStep
                });
            }

            if (gridStep <= 0)
            {
                gridStep = 1.0f;
            }

            foreach (var detail in details)
            {
                _contourBuilderService.BuildGeometry(detail);
                _maskRasterizerService.RasterizeDetail(detail, gridStep, thickness);
            }

            ValidateMaskSizes(details, workpiece, indent);

            var remaining = details.ToList();
            var workpieces = new List<Workpiece2D>();

            while (remaining.Count > 0)
            {
                var resultWorkpiece = _maskPlacementService.PlaceSingleWorkpiece(
                    remaining,
                    workpiece,
                    indent,
                    gridStep,
                    allowRotate90);

                if (resultWorkpiece.Details.Count == 0)
                {
                    throw new Exception("detail > workpiece");
                }

                workpieces.Add(resultWorkpiece);
            }

            var result = new Cutting2DResult
            {
                Workpieces = workpieces,
                TotalPercentUsage = workpieces.Count == 0
                    ? 0
                    : Math.Round(workpieces.Sum(w => w.ProcentUsage) / workpieces.Count, 2),
                Algorithm = useMaskNesting ? "mask" : "mask",
                GridStep = gridStep
            };

            return await Task.FromResult(result);
        }

        private static void ValidateMaskSizes(List<Detail2D> details, Workpiece workpiece, int indent)
        {
            var usableWidth = workpiece.Width - 2 * indent;
            var usableHeight = workpiece.Height - 2 * indent;

            if (usableWidth <= 0 || usableHeight <= 0)
            {
                throw new Exception("workpiece size is too small");
            }

            foreach (var detail in details)
            {
                var fitsNormal = detail.Mask0 != null
                                 && detail.Mask0.WidthCells * detail.Mask0.GridStep <= usableWidth
                                 && detail.Mask0.HeightCells * detail.Mask0.GridStep <= usableHeight;

                var fitsRotated = detail.Mask90 != null
                                  && detail.Mask90.WidthCells * detail.Mask90.GridStep <= usableWidth
                                  && detail.Mask90.HeightCells * detail.Mask90.GridStep <= usableHeight;

                if (!fitsNormal && !fitsRotated)
                {
                    throw new Exception("detail > workpiece");
                }
            }
        }
    }
}