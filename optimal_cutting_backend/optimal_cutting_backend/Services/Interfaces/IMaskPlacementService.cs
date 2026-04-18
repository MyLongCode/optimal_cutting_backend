using vega.Migrations.DAL;
using vega.Models;

namespace vega.Services.Interfaces
{
    public interface IMaskPlacementService
    {
        Workpiece2D PlaceSingleWorkpiece(
            List<Detail2D> remainingDetails,
            Workpiece workpiece,
            int indent,
            float gridStep,
            bool allowRotate90);
    }
}