namespace vega.Models
{
    public class Point
    {
        public float X { get; set; }
        public float Y { get; set; }

        public Point(float x, float y)
        {
            X = x; Y = y;
        }
    }

}
namespace vega.Models
{
    public readonly record struct Point2D(float X, float Y);
}