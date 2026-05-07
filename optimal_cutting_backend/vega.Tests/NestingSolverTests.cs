using NetTopologySuite.Geometries;
using System.Text.Json;
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

        var service = CreateService();
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
            Assert.True(result.PlacedParts[i].TransformedGeometry!.Intersection(result.PlacedParts[j].TransformedGeometry!).Area <= 1e-6);
    }
    [Fact]
    public void ScaledInput_ShouldApplyGapAndReturnOriginalUnits()
    {
        var dto = new Cutting2DNestingDTO
        {
            Scale = 1000,
            Kerf = 2,
            Clearance = 1,
            Sheets =
            [
                new NestingSheetDto
                {
                    Id = "S1",
                    Outer = [[new() { X = 0, Y = 0 }, new() { X = 103, Y = 0 }, new() { X = 103, Y = 50 }, new() { X = 0, Y = 50 }]]
                }
            ],
            Parts =
            [
                new NestingPartDto
                {
                    Id = "P",
                    Quantity = 2,
                    Outer = [[new() { X = 0, Y = 0 }, new() { X = 50, Y = 0 }, new() { X = 50, Y = 50 }, new() { X = 0, Y = 50 }]]
                }
            ],
            AllowedRotationsDegrees = [0]
        };

        var service = CreateService();
        var result = service.Nest(dto);

        Assert.Equal(2, result.PlacedParts.Count);
        Assert.Empty(result.UnplacedParts);
        Assert.All(result.PlacedParts, p => Assert.InRange(p.X, 0d, 103d));
        Assert.All(result.PlacedParts, p => Assert.NotEmpty(p.Contours));
        Assert.True(result.PlacedParts[0].TransformedGeometry!.Distance(result.PlacedParts[1].TransformedGeometry!) >= 3 - 1e-6);
    }

    [Fact]
    public void NestingResult_ShouldSerializeWithoutRawNtsGeometry()
    {
        var dto = new Cutting2DNestingDTO
        {
            Scale = 1000,
            Kerf = 2,
            Clearance = 1,
            Sheets =
            [
                new NestingSheetDto
                {
                    Id = "S1",
                    Outer = [[new() { X = 0, Y = 0 }, new() { X = 103, Y = 0 }, new() { X = 103, Y = 50 }, new() { X = 0, Y = 50 }]]
                }
            ],
            Parts =
            [
                new NestingPartDto
                {
                    Id = "P",
                    Quantity = 1,
                    Outer = [[new() { X = 0, Y = 0 }, new() { X = 50, Y = 0 }, new() { X = 50, Y = 50 }, new() { X = 0, Y = 50 }]]
                }
            ],
            AllowedRotationsDegrees = [0]
        };

        var result = CreateService().Nest(dto);
        var json = JsonSerializer.Serialize(result);

        Assert.Contains("Contours", json);
        Assert.DoesNotContain("TransformedGeometry", json);
        Assert.DoesNotContain("Infinity", json);
    }

    [Fact]
    public void ZeroGap_ShouldAllowPartsToShareCutLineWithoutOverlap()
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
                    Outer = [[new() { X = 0, Y = 0 }, new() { X = 100, Y = 0 }, new() { X = 100, Y = 50 }, new() { X = 0, Y = 50 }]]
                }
            ],
            Parts =
            [
                new NestingPartDto
                {
                    Id = "P",
                    Quantity = 2,
                    Outer = [[new() { X = 0, Y = 0 }, new() { X = 50, Y = 0 }, new() { X = 50, Y = 50 }, new() { X = 0, Y = 50 }]]
                }
            ],
            AllowedRotationsDegrees = [0]
        };

        var service = CreateService();
        var result = service.Nest(dto);

        Assert.Equal(2, result.PlacedParts.Count);
        Assert.Empty(result.UnplacedParts);
        Assert.True(result.PlacedParts[0].TransformedGeometry!.Intersection(result.PlacedParts[1].TransformedGeometry!).Area <= 1e-6);
    }

    private static PolygonNestingService CreateService()
        => new(new GeometryNormalizer(), new PolygonValidator(), new NestingSolver(new NfpService(), new PlacementCandidateGenerator()));

}
