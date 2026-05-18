using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vega.Controllers.DTO;
using vega.Migrations.DAL;
using vega.Migrations.EF;
using vega.Models;
using vega.Models.Nesting;
using vega.Services.Interfaces;
using vega.Controllers.DTO.Nesting;
using vega.Services.Interfaces.Nesting;

namespace vega.Controllers
{
    [Route("/api")]
    public class CuttingController : Controller
    {
        private readonly ICutting1DService _cutting1DService;
        private readonly ICutting2DService _cutting2DService;
        private readonly VegaContext _db;
        private readonly IPolygonNestingService _polygonNestingService;
        private readonly IContourBuilderService _contourBuilderService;
        private readonly ILogger<CuttingController> _logger;

        public CuttingController(ICutting1DService cutting1DService, ICutting2DService cutting2DService, VegaContext db, IPolygonNestingService polygonNestingService, IContourBuilderService contourBuilderService, ILogger<CuttingController> logger)
        {
            _db = db;
            _cutting1DService = cutting1DService;
            _cutting2DService = cutting2DService;
            _polygonNestingService = polygonNestingService;
            _contourBuilderService = contourBuilderService;
            _logger = logger;
        }

        /// <summary>
        /// Method for calculating optimal 1d cutting.
        /// </summary>
        /// <param name="dto"></param>
        /// <returns>Returns the calculated model</returns>
        /// <response code="200">Calculeted is ok</response>
        /// <response code="500">Detail length > workpiece length</response>
        [HttpPost]
        [Route("1d/calculate")]
        public async Task<ActionResult> Calculate1DCutting([FromBody] Calculate1DDTO dto)
        {
            if (dto.Details.Max() > dto.WorkpiecesLength.Max())
                return BadRequest("detail length > workpiece length");

            var res = await _cutting1DService.CalculateCuttingAsync(dto.Details, dto.WorkpiecesLength);
            return Ok(res);
        }

        /// <summary>
        /// Method for calculating optimal 2d cutting.
        /// </summary>
        /// <param name="dto"></param>
        /// <returns>Returns the calculated model</returns>
        /// <response code="200">Calculeted is ok</response>
        /// <response code="500">Detail length > workpiece length</response>
        [HttpPost]
        [Route("2d/calculate")]
        public async Task<ActionResult> Calculate2DCutting([FromBody] Calculate2DDTO dto)
        {
            var details = new List<Detail2D>();
            foreach (var detail in dto.Details)
            {
                for (var i = 0; i < detail.Count; i++)
                {
                    details.Add(new Detail2D
                    {
                        Width = detail.Width,
                        Height = detail.Height,
                        X = 0,
                        Y = 0
                    });
                }
            }

            var workpiece = new Workpiece
            {
                Width = dto.Workpiece.Width,
                Height = dto.Workpiece.Height
            };

            if (details.Max(d => d.Width) > workpiece.Width || details.Max(d => d.Height) > Math.Max(workpiece.Height, workpiece.Width))
            {
                return BadRequest("detail > workpiece");
            }

            var res = await _cutting2DService.CalculateCuttingAsync(
                details,
                workpiece,
                dto.CuttingThickness,
                dto.Indent
            );

            return Ok(res);
        }

        /// <summary>
        /// Method for calculating optimal 2d cutting with dxf
        /// </summary>
        /// <param name="dto"></param>
        /// <returns>Returns the calculated model</returns>
        /// <response code="200">Calculeted is ok</response>
        /// <response code="500">Detail length > workpiece length</response>
        [HttpPost]
        [Route("dxf/calculate")]
        public async Task<ActionResult> Calculate2DCuttingWithDXF([FromBody] Calculate2DDXFDTO dto)
        {
            var details = dto.DetailsId
                .Select(d => _db.Filenames.Include(f => f.Figures).FirstOrDefault(f => f.Id == d))
                .Where(d => d != null)
                .Select(d => new Detail2D(d!.Figures, d.Designation))
                .ToList();

            if (details.Count == 0)
            {
                return BadRequest("details is null");
            }

            var workpiece = new Workpiece
            {
                Height = dto.Workpiece.Height,
                Width = dto.Workpiece.Width
            };

            if (details.Max(d => d.Width) > workpiece.Width || details.Max(d => d.Height) > Math.Max(workpiece.Height, workpiece.Width))
            {
                return BadRequest("detail > workpiece");
            }

            var res = await _cutting2DService.CalculateCuttingAsync(
                details,
                workpiece,
                dto.CuttingThickness,
                dto.Indent,
                dto.GridStep,
                dto.AllowRotate90,
                dto.UseMaskNesting
            );

            return Ok(res);
        }

        [HttpPost]
        [Route("cutting2d/nesting")]
        public ActionResult CalculatePolygonNesting([FromBody] Cutting2DNestingDTO dto)
        {
            var res = _polygonNestingService.Nest(dto);
            return Ok(res);
        }

        [HttpPost]
        [Route("cutting2d/nesting/from-db")]
        public ActionResult CalculatePolygonNestingFromDb([FromBody] Cutting2DNestingFromDbDTO dto)
        {
            if (dto.DetailIds.Count == 0)
            {
                return BadRequest(new
                {
                    error = "detailIds is empty",
                    diagnostics = new NestingDiagnostics()
                });
            }

            var groupedIds = dto.DetailIds.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            var files = _db.Filenames
                .Include(f => f.Figures)
                .Where(f => groupedIds.Keys.Contains(f.Id))
                .ToList();

            var foundIds = files.Select(f => f.Id).OrderBy(x => x).ToList();
            var missingIds = groupedIds.Keys.Except(foundIds).OrderBy(x => x).ToList();
            var diagnostics = new NestingDiagnostics
            {
                RequestedDetailIds = dto.DetailIds.ToList(),
                FoundDetailIds = foundIds,
                MissingDetailIds = missingIds
            };

            _logger.LogInformation(
                "cutting2d/nesting/from-db requested details: {RequestedDetailIds}; found: {FoundDetailIds}; missing: {MissingDetailIds}",
                string.Join(",", diagnostics.RequestedDetailIds),
                string.Join(",", diagnostics.FoundDetailIds),
                string.Join(",", diagnostics.MissingDetailIds));

            if (missingIds.Count > 0)
            {
                return BadRequest(new
                {
                    error = "some details were not found",
                    missingDetailIds = missingIds,
                    diagnostics
                });
            }

            var parts = new List<NestingPartDto>();
            foreach (var file in files)
            {
                if (file.Figures.Count == 0)
                {
                    AddInvalidDetail(diagnostics, file, "Figures is empty.");
                    continue;
                }

                var detail = new Detail2D(file.Figures, file.Designation);
                try
                {
                    _contourBuilderService.BuildGeometry(detail);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to build contour for detail {DetailId} ({Designation})", file.Id, file.Designation);
                    AddInvalidDetail(diagnostics, file, ex.Message);
                    continue;
                }

                if (detail.Contour == null)
                {
                    AddInvalidDetail(diagnostics, file, "Contour was not built.");
                    continue;
                }

                if (detail.Contour.FilledContours.Count == 0)
                {
                    AddInvalidDetail(diagnostics, file, "Contour does not contain filled contours.");
                    continue;
                }

                var orderedFilled = detail.Contour.FilledContours
                    .Where(loop => loop.Count >= 3)
                    .OrderByDescending(GetPolygonAbsArea)
                    .ToList();

                if (orderedFilled.Count == 0)
                {
                    AddInvalidDetail(diagnostics, file, "Contour does not contain a valid outer loop.");
                    continue;
                }

                var outerLoop = EnsureCounterClockwise(orderedFilled.First());
                var innerFilledAsHoles = orderedFilled.Skip(1).ToList();
                var allHoles = innerFilledAsHoles.Concat(detail.Contour.HoleContours).ToList();

                parts.Add(new NestingPartDto
                {
                    Id = file.Id.ToString(),
                    Quantity = groupedIds[file.Id],
                    Outer = new List<List<NestingPointDto>>
                    {
                        ToNestingLoop(outerLoop)
                    },
                    Holes = allHoles
                        .Where(loop => loop.Count >= 3)
                        .Select(loop => new List<List<NestingPointDto>>
                        {
                            ToNestingLoop(EnsureClockwise(loop))
                        })
                        .ToList()
                });
            }

            diagnostics.GeneratedParts = parts.Count;
            diagnostics.GeneratedPartInstances = parts.Sum(p => p.Quantity);

            _logger.LogInformation(
                "cutting2d/nesting/from-db generated {GeneratedParts} part types / {GeneratedPartInstances} instances; invalid details: {InvalidDetailCount}",
                diagnostics.GeneratedParts,
                diagnostics.GeneratedPartInstances,
                diagnostics.InvalidDetails.Count);

            if (parts.Count == 0)
            {
                return BadRequest(new
                {
                    error = "cannot build contours from details",
                    diagnostics
                });
            }

            var nestingDto = new Cutting2DNestingDTO
            {
                Sheets = dto.Sheets,
                Parts = parts,
                Kerf = dto.Kerf,
                Clearance = dto.Clearance,
                Scale = dto.Scale,
                EnableLocalSearch = dto.EnableLocalSearch,
                AllowedRotationsDegrees = dto.AllowedRotationsDegrees
            };

            NestingResult res;
            try
            {
                res = _polygonNestingService.Nest(nestingDto);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Polygon nesting failed for details {RequestedDetailIds}", string.Join(",", diagnostics.RequestedDetailIds));
                return BadRequest(new
                {
                    error = "nesting failed",
                    message = ex.Message,
                    diagnostics
                });
            }

            diagnostics.PlacedParts = res.PlacedParts.Count;
            diagnostics.UnplacedParts = res.UnplacedParts.Count;
            res.Diagnostics = diagnostics;

            _logger.LogInformation(
                "cutting2d/nesting/from-db solver returned {PlacedParts} placed and {UnplacedParts} unplaced parts",
                diagnostics.PlacedParts,
                diagnostics.UnplacedParts);

            return Ok(res);
        }

        private static void AddInvalidDetail(NestingDiagnostics diagnostics, Filename file, string reason)
        {
            diagnostics.InvalidDetails.Add(new NestingDetailDiagnostic
            {
                DetailId = file.Id,
                Designation = file.Designation,
                Name = file.Name,
                Reason = reason
            });
        }

        private static List<NestingPointDto> ToNestingLoop(List<Point2D> loop)
            => TrimClosingPoint(loop)
                .Select(p => new NestingPointDto { X = p.X, Y = p.Y })
                .ToList();

        private static List<Point2D> EnsureCounterClockwise(List<Point2D> loop)
            => GetPolygonSignedArea(loop) >= 0 ? loop : loop.AsEnumerable().Reverse().ToList();

        private static List<Point2D> EnsureClockwise(List<Point2D> loop)
            => GetPolygonSignedArea(loop) <= 0 ? loop : loop.AsEnumerable().Reverse().ToList();

        private static List<Point2D> TrimClosingPoint(List<Point2D> loop)
        {
            if (loop.Count > 1 && Math.Abs(loop[0].X - loop[^1].X) < 0.0001 && Math.Abs(loop[0].Y - loop[^1].Y) < 0.0001)
            {
                return loop.Take(loop.Count - 1).ToList();
            }

            return loop;
        }

        private static double GetPolygonAbsArea(List<Point2D> loop)
            => Math.Abs(GetPolygonSignedArea(loop));

        private static double GetPolygonSignedArea(List<Point2D> loop)
        {
            if (loop.Count < 3) return 0;
            var points = TrimClosingPoint(loop);
            double area = 0;
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                area += (a.X * b.Y) - (b.X * a.Y);
            }
            return area * 0.5;
        }

    }
}
