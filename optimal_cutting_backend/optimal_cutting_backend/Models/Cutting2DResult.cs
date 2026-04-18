namespace vega.Models
{
    public class Cutting2DResult
    {
        public List<Workpiece2D> Workpieces { get; set; } = new();
        public double TotalPercentUsage { get; set; }
        public string Algorithm { get; set; } = "mask";
        public float GridStep { get; set; }
    }

    public class Workpiece2D
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public List<Detail2D> Details { get; set; } = new();
        public double ProcentUsage { get; set; }
    }
}