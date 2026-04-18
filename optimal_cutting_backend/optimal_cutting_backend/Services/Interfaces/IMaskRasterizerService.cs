using vega.Models;

namespace vega.Services.Interfaces
{
    public interface IMaskRasterizerService
    {
        void RasterizeDetail(Detail2D detail, float gridStep, float clearance);
    }
}