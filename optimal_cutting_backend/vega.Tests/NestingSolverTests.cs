using NetTopologySuite.Geometries;
using vega.Controllers.DTO.Nesting;
using vega.Services.Nesting;
using Xunit;

namespace vega.Tests;

public class NestingSolverTests
{
    [Fact]
    public void LShapedSheet_ShouldPlaceAtLeastOnePartInConcavePocket()
    {
        var dto = new Cutting2DNestingDTO
        {
            Scale = 1,
            Kerf = 0,
            Clearance = 0,
            Sheets =
            [
                new NestingSheetDto
                {
                    Id = "S1",
                    Outer = [
                        [ new() { X = 0, Y = 0 }, new() { X = 100, Y = 0 }, new() { X = 100, Y = 20 }, new() { X = 40, Y = 20 }, new() { X = 40, Y = 100 }, new() { X = 0, Y = 100 } ]
                    ]
                }
            ],
            Parts =
            [
                new NestingPartDto
                {
                    Id = "P",
                    Quantity = 3,
                    Outer = [[new() { X = 0, Y = 0 }, new() { X = 18, Y = 0 }, new() { X = 18, Y = 18 }, new() { X = 0, Y = 18 }]]
                }
            ]
        };

        var service = new PolygonNestingService(new GeometryNormalizer(), new PolygonValidator(), new NestingSolver(new NfpService(), new PlacementCandidateGenerator()));
        var result = service.Nest(dto);

        Assert.NotEmpty(result.PlacedParts);
        Assert.Contains(result.PlacedParts, p => p.X >= 40 && p.Y <= 20);

        var sheet = new GeometryFactory().CreatePolygon(new[]
        {
            new Coordinate(0,0),new Coordinate(100,0),new Coordinate(100,20),new Coordinate(40,20),new Coordinate(40,100),new Coordinate(0,100),new Coordinate(0,0)
        });

        foreach (var placed in result.PlacedParts)
        {
            Assert.True(sheet.Covers(placed.TransformedGeometry));
        }

        for (int i = 0; i < result.PlacedParts.Count; i++)
        for (int j = i + 1; j < result.PlacedParts.Count; j++)
            Assert.False(result.PlacedParts[i].TransformedGeometry!.Intersects(result.PlacedParts[j].TransformedGeometry!));
    }
}
