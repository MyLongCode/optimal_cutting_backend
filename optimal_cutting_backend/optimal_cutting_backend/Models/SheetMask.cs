namespace vega.Models
{
    public class SheetMask
    {
        public byte[][] Occupancy { get; set; } = Array.Empty<byte[]>();
        public int WidthCells { get; set; }
        public int HeightCells { get; set; }
        public float GridStep { get; set; }
        public int Indent { get; set; }
    }
}