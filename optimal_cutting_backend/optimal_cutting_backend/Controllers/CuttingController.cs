using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vega.Controllers.DTO;
using vega.Migrations.DAL;
using vega.Migrations.EF;
using vega.Models;
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

        public CuttingController(ICutting1DService cutting1DService, ICutting2DService cutting2DService, VegaContext db, IPolygonNestingService polygonNestingService, IContourBuilderService contourBuilderService)
        {
            _db = db;
            _cutting1DService = cutting1DService;
            _cutting2DService = cutting2DService;
            _polygonNestingService = polygonNestingService;
            _contourBuilderService = contourBuilderService;
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
            if (dto.DetailIds.Count == 0) return BadRequest("detailIds is empty");

            var groupedIds = dto.DetailIds.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            var files = _db.Filenames
                .Include(f => f.Figures)
                .Where(f => groupedIds.Keys.Contains(f.Id))
                .ToList();

            if (files.Count == 0) return BadRequest("details not found");

            var parts = new List<NestingPartDto>();
            foreach (var file in files)
            {
                var detail = new Detail2D(file.Figures, file.Designation);
                _contourBuilderService.BuildGeometry(detail);
                if (detail.Contour == null || detail.Contour.FilledContours.Count == 0) continue;

                var orderedFilled = detail.Contour.FilledContours
                    .OrderByDescending(GetPolygonAbsArea)
                    .ToList();
                var outerLoop = orderedFilled.First();
                var innerFilledAsHoles = orderedFilled.Skip(1).ToList();
                var allHoles = innerFilledAsHoles.Concat(detail.Contour.HoleContours).ToList();

                parts.Add(new NestingPartDto
                {
                    Id = file.Id.ToString(),
                    Quantity = groupedIds[file.Id],
                    Outer = new List<List<NestingPointDto>>
                    {
                        outerLoop.Select(p => new NestingPointDto { X = p.X, Y = p.Y }).ToList()
                    },
                    Holes = allHoles
                        .Select(loop => new List<List<NestingPointDto>>
                        {
                            loop.Select(p => new NestingPointDto { X = p.X, Y = p.Y }).ToList()
                        })
                        .ToList()
                });
            }

            if (parts.Count == 0) return BadRequest("cannot build contours from details");

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

            var res = _polygonNestingService.Nest(nestingDto);
            return Ok(res);
        }

        private static double GetPolygonAbsArea(List<Point2D> loop)
        {
            if (loop.Count < 3) return 0;
            double area = 0;
            for (var i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                area += (a.X * b.Y) - (b.X * a.Y);
            }
            return Math.Abs(area) * 0.5;
        }

    }
}
