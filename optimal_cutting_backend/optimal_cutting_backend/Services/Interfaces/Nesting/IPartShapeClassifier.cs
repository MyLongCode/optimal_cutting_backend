using vega.Models.Nesting;

namespace vega.Services.Interfaces.Nesting;

public interface IPartShapeClassifier
{
    ClassifiedPart Classify(NormalizedPolygon part);
}
