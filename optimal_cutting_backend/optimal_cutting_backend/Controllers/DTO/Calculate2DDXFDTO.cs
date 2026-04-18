using vega.Models;

namespace vega.Controllers.DTO
{
    public class Calculate2DDXFDTO
    {
        public List<int> DetailsId { get; set; } = new();
        public Workpiece2DDTO Workpiece { get; set; } = null!;
        public float CuttingThickness { get; set; }
        public int Indent { get; set; }
        public float GridStep { get; set; } = 1.0f;
        public bool AllowRotate90 { get; set; } = true;
        public bool UseMaskNesting { get; set; } = true;
    }
}