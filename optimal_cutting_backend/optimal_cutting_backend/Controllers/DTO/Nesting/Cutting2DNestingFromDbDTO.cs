namespace vega.Controllers.DTO.Nesting;

public class Cutting2DNestingFromDbDTO
{
    public required List<int> DetailIds { get; set; }
    public required List<NestingSheetDto> Sheets { get; set; }
    public double Kerf { get; set; }
    public double Clearance { get; set; }
    public int Scale { get; set; } = 1000;
    public bool EnableLocalSearch { get; set; }
    public List<int>? AllowedRotationsDegrees { get; set; }
}
