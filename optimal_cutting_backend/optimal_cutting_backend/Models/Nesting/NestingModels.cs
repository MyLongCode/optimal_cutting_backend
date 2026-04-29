using NetTopologySuite.Geometries;

namespace vega.Models.Nesting;

public record NormalizedPolygon(string Id, Polygon Polygon, bool IsSheet = false);

public class NestingPlacement
{
    public required string PartId { get; set; }
    public required string SheetId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public Geometry? TransformedGeometry { get; set; }
}

public class NestingResult
{
    public List<string> Sheets { get; set; } = new();
    public List<NestingPlacement> PlacedParts { get; set; } = new();
    public List<string> UnplacedParts { get; set; } = new();
    public Dictionary<string, double> UtilizationBySheet { get; set; } = new();
    public double TotalUtilization { get; set; }
    public string Svg { get; set; } = string.Empty;
    public string Dxf { get; set; } = string.Empty;
}
