namespace vega.Models
{
    public class DetailMask
    {
        public byte[][] Cells { get; set; } = Array.Empty<byte[]>();
        public int WidthCells { get; set; }
        public int HeightCells { get; set; }
        public int OriginOffsetXCells { get; set; }
        public int OriginOffsetYCells { get; set; }
        public int PartOccupiedCells { get; set; }
        public float GridStep { get; set; }
    }
}