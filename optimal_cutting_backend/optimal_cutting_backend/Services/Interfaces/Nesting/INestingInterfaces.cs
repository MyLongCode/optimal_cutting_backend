using NetTopologySuite.Geometries;
using vega.Controllers.DTO.Nesting;
using vega.Models.Nesting;

namespace vega.Services.Interfaces.Nesting;

public interface IGeometryNormalizer
{
    NormalizedPolygon Normalize(string id, List<List<NestingPointDto>> outer, List<List<List<NestingPointDto>>> holes, int scale, bool isSheet);
}

public interface IPolygonValidator
{
    void ValidatePolygon(Polygon polygon, string id);
}

public interface INfpService
{
    Geometry BuildOuterNfp(Polygon a, Polygon b, double offset);
    Geometry BuildInnerNfp(Polygon sheet, Polygon part, double offset);
}

public interface IPlacementCandidateGenerator
{
    IEnumerable<Coordinate> GenerateCandidates(Geometry innerNfp, IEnumerable<Geometry>? forbidden = null);
}

public interface INestingSolver
{
    NestingResult Solve(List<NormalizedPolygon> sheets, List<NormalizedPolygon> parts, double kerf, double clearance, bool localSearch, IReadOnlyList<int>? rotations = null);
}

public interface IPolygonNestingService
{
    NestingResult Nest(Cutting2DNestingDTO dto);
}
