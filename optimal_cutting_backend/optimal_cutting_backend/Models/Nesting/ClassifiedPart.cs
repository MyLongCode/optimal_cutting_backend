namespace vega.Models.Nesting;

public enum NestingShapeKind
{
    Rectangle,
    Complex
}

public sealed record ClassifiedPart(
    NormalizedPolygon Source,
    NestingShapeKind Kind,
    double Width,
    double Height);
