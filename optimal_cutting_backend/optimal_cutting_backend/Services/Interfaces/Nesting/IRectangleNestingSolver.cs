using vega.Models.Nesting;

namespace vega.Services.Interfaces.Nesting;

public interface IRectangleNestingSolver
{
    List<NestingPlacement> SolveRectangles(
        List<NormalizedPolygon> sheets,
        List<ClassifiedPart> rectangles,
        double gap,
        IReadOnlyList<int> rotations);
}
