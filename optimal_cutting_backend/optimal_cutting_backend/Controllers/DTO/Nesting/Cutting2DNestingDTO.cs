namespace vega.Controllers.DTO.Nesting;

public class Cutting2DNestingDTO
{
    public required List<NestingSheetDto> Sheets { get; set; }
    public required List<NestingPartDto> Parts { get; set; }
    public double Kerf { get; set; }
    public double Clearance { get; set; }
    public int Scale { get; set; } = 1000;
    public bool EnableLocalSearch { get; set; }
    public List<int>? AllowedRotationsDegrees { get; set; }
}

public class NestingSheetDto
{
    public required string Id { get; set; }
    public required List<List<NestingPointDto>> Outer { get; set; }
    public List<List<List<NestingPointDto>>> Holes { get; set; } = new();
}

public class NestingPartDto
{
    public required string Id { get; set; }
    public int Quantity { get; set; } = 1;
    public required List<List<NestingPointDto>> Outer { get; set; }
    public List<List<List<NestingPointDto>>> Holes { get; set; } = new();
}

public class NestingPointDto
{
    public double X { get; set; }
    public double Y { get; set; }
}
