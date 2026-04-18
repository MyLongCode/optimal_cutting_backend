using vega.Migrations.DAL;
using vega.Models;

namespace vega.Services.Interfaces
{
    public interface ICutting2DService
    {
        Task<Cutting2DResult> CalculateCuttingAsync(
            List<Detail2D> details,
            Workpiece workpiece,
            float thickness,
            int indent,
            float gridStep = 1.0f,
            bool allowRotate90 = true,
            bool useMaskNesting = true);
    }
}