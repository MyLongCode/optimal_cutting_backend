using NetTopologySuite.Geometries;
using System.Text.Json.Serialization;
using vega.Models;

namespace vega.Models.Nesting;

public record NormalizedPolygon(string Id, Polygon Polygon, bool IsSheet = false);

public class NestingPlacement
{
    public required string PartId { get; set; }
    public required string SheetId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public int Rotation { get; set; }
    public List<List<NestingOutputPoint>> Contours { get; set; } = new();

    [JsonIgnore]
    public Geometry? TransformedGeometry { get; set; }
}

public class NestingOutputPoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

public class NestingDetailDiagnostic
{
    public int DetailId { get; set; }
    public string? Designation { get; set; }
    public string? Name { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class NestingDiagnostics
{
    public List<int> RequestedDetailIds { get; set; } = new();
    public List<int> FoundDetailIds { get; set; } = new();
    public List<int> MissingDetailIds { get; set; } = new();
    public List<NestingDetailDiagnostic> InvalidDetails { get; set; } = new();
    public int GeneratedParts { get; set; }
    public int GeneratedPartInstances { get; set; }
    public int PlacedParts { get; set; }
    public int UnplacedParts { get; set; }
}

public class NestingResult
{
    public List<string> Sheets { get; set; } = new();
    public List<Workpiece2D> Workpieces { get; set; } = new();
    public List<NestingPlacement> PlacedParts { get; set; } = new();
    public List<string> UnplacedParts { get; set; } = new();
    public Dictionary<string, double> UtilizationBySheet { get; set; } = new();
    public double TotalUtilization { get; set; }
    public string Svg { get; set; } = string.Empty;
    public string Dxf { get; set; } = string.Empty;
    public NestingDiagnostics? Diagnostics { get; set; }
}
