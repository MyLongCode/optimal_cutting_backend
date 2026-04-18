namespace vega.Models
{
    public class Contour2D
    {
        public List<List<Point2D>> FilledContours { get; set; } = new();
        public List<List<Point2D>> HoleContours { get; set; } = new();
        public float MinX { get; set; }
        public float MinY { get; set; }
        public float MaxX { get; set; }
        public float MaxY { get; set; }

        public float GetWidth() => Math.Max(0, MaxX - MinX);
        public float GetHeight() => Math.Max(0, MaxY - MinY);

        public IEnumerable<Point2D> GetAllPoints()
        {
            foreach (var contour in FilledContours)
            {
                foreach (var point in contour)
                {
                    yield return point;
                }
            }

            foreach (var contour in HoleContours)
            {
                foreach (var point in contour)
                {
                    yield return point;
                }
            }
        }

        public Contour2D DeepClone()
        {
            return new Contour2D
            {
                FilledContours = FilledContours.Select(c => c.ToList()).ToList(),
                HoleContours = HoleContours.Select(c => c.ToList()).ToList(),
                MinX = MinX,
                MinY = MinY,
                MaxX = MaxX,
                MaxY = MaxY
            };
        }
    }
}