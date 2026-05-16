using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SkiaSharp;
using vega.Controllers;
using vega.Migrations.DAL;
using vega.Services.Interfaces;
using NetTopologySuite.Geometries;
using System.Text.Json;
using vega.Controllers.DTO.Nesting;
using vega.Models;
using vega.Services;
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
        Assert.Single(result.Workpieces);
        Assert.Equal(2, result.Workpieces[0].Details.Count);
        Assert.All(result.Workpieces[0].Details, d => Assert.NotNull(d.Contour));
        Assert.True(result.PlacedParts[0].TransformedGeometry!.Distance(result.PlacedParts[1].TransformedGeometry!) >= 3 - 1e-6);
    }

    [Fact]
    public async Task NestingWorkpieces_ShouldBeDrawableBy2DExportRenderer()
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
                    Quantity = 1,
                    Outer = [[new() { X = 0, Y = 0 }, new() { X = 50, Y = 0 }, new() { X = 50, Y = 50 }, new() { X = 0, Y = 50 }]]
                }
            ],
            AllowedRotationsDegrees = [0]
        };

        var nestingResult = CreateService().Nest(dto);
        var cuttingResult = new Cutting2DResult { Workpieces = nestingResult.Workpieces };
        var images = await new DrawService().Draw2DCuttingAsync(cuttingResult);

        Assert.Single(images);
        Assert.NotEmpty(images[0]);
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
        Assert.Contains("Workpieces", json);
        Assert.DoesNotContain("TransformedGeometry", json);
        Assert.DoesNotContain("Infinity", json);

        var exportPayload = JsonSerializer.Deserialize<Cutting2DResult>(json);
        Assert.NotNull(exportPayload);
        Assert.NotEmpty(exportPayload.Workpieces);
        Assert.NotEmpty(exportPayload.Workpieces[0].Details);
    }


    [Fact]
    public void NestingSvg_ShouldUseInvariantDecimalsAndPositiveViewBox()
    {
        var dto = new Cutting2DNestingDTO
        {
            Scale = 10,
            Kerf = 0,
            Clearance = 0,
            Sheets =
            [
                new NestingSheetDto
                {
                    Id = "S1",
                    Outer = [[new() { X = 0, Y = 0 }, new() { X = 111.1, Y = 0 }, new() { X = 111.1, Y = 111.1 }, new() { X = 0, Y = 111.1 }]]
                }
            ],
            Parts =
            [
                new NestingPartDto
                {
                    Id = "P",
                    Quantity = 1,
                    Outer = [[new() { X = 0, Y = 0 }, new() { X = 10.5, Y = 0 }, new() { X = 10.5, Y = 10.5 }, new() { X = 0, Y = 10.5 }]]
                }
            ],
            AllowedRotationsDegrees = [0]
        };

        var result = CreateService().Nest(dto);

        Assert.StartsWith("<svg", result.Svg);
        Assert.Contains("width='111.1'", result.Svg);
        Assert.Contains("height='111.1'", result.Svg);
        Assert.Contains("viewBox='0 0 111.1 111.1'", result.Svg);
        Assert.Contains("10.5", result.Svg);
        Assert.DoesNotContain("-111.1", result.Svg);
        Assert.DoesNotContain("10,5", result.Svg);
    }

    [Fact]
    public async Task Draw2DCuttingAsync_ShouldUseWorkpieceSizeForPreviewCanvas()
    {
        var result = new Cutting2DResult
        {
            Workpieces =
            [
                new Workpiece2D
                {
                    Width = 1111,
                    Height = 1111,
                    Details = [new Detail2D { X = 0, Y = 0, Width = 10, Height = 10 }]
                }
            ]
        };

        var images = await new DrawService().Draw2DCuttingAsync(result);
        using var bitmap = SKBitmap.Decode(images[0]);

        Assert.Equal(1111, bitmap.Width);
        Assert.Equal(1111, bitmap.Height);
    }

    [Fact]
    public async Task ExportPng_ShouldReturnPngPreviewAndZipExport()
    {
        var controller = new FileController(
            new StubCsvService(),
            new StubDrawService(),
            new StubDxfService(),
            null!,
            new HttpContextAccessor());
        var payload = new Cutting2DResult { Workpieces = [new Workpiece2D { Width = 10, Height = 10 }] };

        var preview = Assert.IsType<FileContentResult>(await controller.ExportPng(payload, preview: true));
        var export = Assert.IsType<FileContentResult>(await controller.ExportPng(payload, preview: false));

        Assert.Equal("image/png", preview.ContentType);
        Assert.Equal("application/zip", export.ContentType);
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
        => new(
            new GeometryNormalizer(),
            new PolygonValidator(),
            new NestingSolver(new NfpService(), new PlacementCandidateGenerator()),
            new PartShapeClassifier(),
            new RectangleNestingSolver());

}

public sealed class StubDrawService : IDrawService
{
    public Task<byte[]> DrawDXFAsync(List<Figure> figures) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> Draw1DCuttingAsync(Cutting1DResult result) => Task.FromResult(Array.Empty<byte>());
    public Task<List<byte[]>> Draw2DCuttingAsync(Cutting2DResult result) => Task.FromResult(new List<byte[]> { new byte[] { 1, 2, 3 } });
    public Task<List<byte[]>> DrawDXFCuttingAsync(Cutting2DResult result) => Task.FromResult(new List<byte[]>());
    public Task<List<byte[]>> DrawDXFCuttingForPDF(Cutting2DResult result) => Task.FromResult(new List<byte[]>());
}

public sealed class StubCsvService : ICSVService
{
    public IEnumerable<T> ReadCSV<T>(Stream file) => [];
    public byte[] WriteCSV<T>(IEnumerable<T> items) => [];
}

public sealed class StubDxfService : IDXFService
{
    public Task<List<Figure>> GetDXFAsync(byte[] fileBytes) => Task.FromResult(new List<Figure>());
    public Task<List<byte[]>> CreateDXFAsync(Cutting2DResult result) => Task.FromResult(new List<byte[]>());
    public Task<List<byte[]>> Create2DDXFAsync(Cutting2DResult result) => Task.FromResult(new List<byte[]>());
}
