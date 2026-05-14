using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using vega.Controllers;
using vega.Controllers.DTO.Nesting;
using vega.Migrations.DAL;
using vega.Migrations.EF;
using vega.Models.Nesting;
using vega.Services;
using vega.Services.Nesting;
using Xunit;

namespace vega.Tests;

public class CuttingControllerNestingFromDbTests
{
    [Fact]
    public void CalculatePolygonNestingFromDb_WithValidDbDetail_ReturnsPlacementsForFrontend()
    {
        using var db = CreateContext();
        SeedRectangleDetail(db, 5);
        var controller = CreateController(db);

        var result = controller.CalculatePolygonNestingFromDb(CreateRequest(5));

        var ok = Assert.IsType<OkObjectResult>(result);
        var nestingResult = Assert.IsType<NestingResult>(ok.Value);
        Assert.NotEmpty(nestingResult.PlacedParts);
        Assert.Empty(nestingResult.UnplacedParts);

        var placement = Assert.Single(nestingResult.PlacedParts);
        Assert.Equal("custom-sheet-1", placement.SheetId);
        Assert.StartsWith("5_", placement.PartId);
        Assert.True(double.IsFinite(placement.X));
        Assert.True(double.IsFinite(placement.Y));
        Assert.Contains(placement.Rotation, new[] { 0, 90, 180, 270 });
        Assert.NotEmpty(placement.Contours);
        Assert.All(placement.Contours, Assert.NotEmpty);

        Assert.NotNull(nestingResult.Diagnostics);
        Assert.Equal(new[] { 5 }, nestingResult.Diagnostics!.RequestedDetailIds);
        Assert.Equal(new[] { 5 }, nestingResult.Diagnostics.FoundDetailIds);
        Assert.Empty(nestingResult.Diagnostics.MissingDetailIds);
        Assert.Empty(nestingResult.Diagnostics.InvalidDetails);
        Assert.Equal(1, nestingResult.Diagnostics.PlacedParts);
    }

    [Fact]
    public void CalculatePolygonNestingFromDb_WithMissingDetail_ReturnsJsonDiagnostics()
    {
        using var db = CreateContext();
        var controller = CreateController(db);

        var result = controller.CalculatePolygonNestingFromDb(CreateRequest(404));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var responseJson = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);

        Assert.Contains("some details were not found", responseJson);
        Assert.Contains("missingDetailIds", responseJson);
        Assert.Contains("404", responseJson);
    }

    private static CuttingController CreateController(VegaContext db)
    {
        var nestingService = new PolygonNestingService(
            new GeometryNormalizer(),
            new PolygonValidator(),
            new NestingSolver(new NfpService(), new PlacementCandidateGenerator()));

        return new CuttingController(
            null!,
            null!,
            db,
            nestingService,
            new ContourBuilderService(),
            NullLogger<CuttingController>.Instance);
    }

    private static VegaContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<VegaContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new VegaContext(options);
    }

    private static void SeedRectangleDetail(VegaContext db, int id)
    {
        var file = new Filename
        {
            Id = id,
            FileName = "rectangle.dxf",
            Designation = "RECT-100x100",
            Name = "Rectangle",
            Thickness = 1,
            UserId = 1,
            MaterialId = 1,
            Figures =
            [
                new Figure { Id = 1, FilenameId = id, TypeId = 1, Coordinates = "0;0;100;0" },
                new Figure { Id = 2, FilenameId = id, TypeId = 1, Coordinates = "100;0;100;100" },
                new Figure { Id = 3, FilenameId = id, TypeId = 1, Coordinates = "100;100;0;100" },
                new Figure { Id = 4, FilenameId = id, TypeId = 1, Coordinates = "0;100;0;0" }
            ]
        };

        db.Filenames.Add(file);
        db.SaveChanges();
    }

    private static Cutting2DNestingFromDbDTO CreateRequest(int detailId)
        => new()
        {
            DetailIds = [detailId],
            Sheets =
            [
                new NestingSheetDto
                {
                    Id = "custom-sheet-1",
                    Outer =
                    [
                        [
                            new() { X = 0, Y = 0 },
                            new() { X = 1111, Y = 0 },
                            new() { X = 1111, Y = 1111 },
                            new() { X = 0, Y = 1111 }
                        ]
                    ],
                    Holes = []
                }
            ],
            Kerf = 1,
            Clearance = 1,
            Scale = 1000,
            EnableLocalSearch = true,
            AllowedRotationsDegrees = [0, 90, 180, 270]
        };
}
