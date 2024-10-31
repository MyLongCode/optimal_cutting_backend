using Microsoft.AspNetCore.Mvc;
using Org.BouncyCastle.Crypto.Prng;
using vega.Controllers.DTO;
using vega.Models;
using vega.Services.Interfaces;

namespace vega.Controllers
{
    public class CuttingController : Controller
    {
        private readonly ICutting1DService _cutting1DService;
        private readonly ICutting2DService _cutting2DService;
        public CuttingController(ICutting1DService cutting1DService, ICutting2DService cutting2DService)
        {
            _cutting1DService = cutting1DService;
            _cutting2DService = cutting2DService;
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
            var res = await _cutting1DService.CalculateCuttingAsync(dto.Details, dto.WorkpieceLength);
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
                for (var i = 0; i < detail.Count; i++)
                    details.Add(new Detail2D()
                    {
                        Width = detail.Width,
                        Height = detail.Height,
                        X = 0,
                        Y = 0,
                    });
            var res = await _cutting2DService.CalculateCuttingAsync(details, dto.Workpiece);
            return Ok(res);
        }
    }
}
