using Microsoft.Extensions.Hosting;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;
using Xunit;

namespace SolidWorksMcp.LiveTests;

public class SurfaceBoundaryContractTests
{
    [Fact]
    public void Surface_boundary_requires_both_ordered_curve_directions()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new());
        ServerSetup.Configure(builder, fileLog: false);
        using var host = builder.Build();

        var tool = Assert.Single(ServerSetup.ListedTools(host.Services), item => item.Name == "surface_boundary");
        Assert.Equal(("bodies", false), Toolsets.Catalog["surface_boundary"]);
        var required = tool.InputSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("direction1", required);
        Assert.Contains("direction2", required);
    }
}

public class SurfaceBoundaryLiveTests(LiveFixture fx) : LiveTest(fx)
{
    [Fact]
    public async Task Two_curves_per_direction_create_a_native_600_square_mm_patch()
    {
        string doc = await BoundaryPartAsync("boundary_sheet");

        var bottom = await LineAsync(doc, [0, 0, 0], [20, 0, 0]);
        var top = await LineAsync(doc, [0, 30, 0], [20, 30, 0]);
        var left = await LineAsync(doc, [0, 0, 0], [0, 30, 0]);
        var right = await LineAsync(doc, [20, 0, 0], [20, 30, 0]);
        var detached = await LineAsync(doc, [0, 2, 0], [0, 30, 0]);
        var features = new InspectService(S);
        int before = (await features.FeatureListAsync(doc, false, 100, 0)).Total;
        var service = new AdvancedFeatureService(S);

        var error = await Assert.ThrowsAsync<SwException>(() => service.SurfaceBoundaryAsync(doc, [bottom, top], [detached, right]));
        Assert.Equal(ErrorCodes.InvalidArgument, error.Code);
        Assert.Equal(before, (await features.FeatureListAsync(doc, false, 100, 0)).Total);

        var result = await service.SurfaceBoundaryAsync(doc, [bottom, top], [left, right], "Verified boundary patch");

        Assert.Equal("NetBlend", result.Feature.Type);
        Assert.Equal("Verified boundary patch", result.Feature.Name);
        Assert.Equal(2, result.Model.Bodies);
        Assert.Equal(1000, result.Model.VolumeMm3, 1e-3);
        var sheet = Assert.Single(result.Bodies!);
        Assert.Equal("surface", sheet.Type);
        Assert.Equal(600, sheet.AreaMm2, 1e-3);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Parallel_bowed_splines_create_a_nonplanar_sheet_between_the_rails(bool withReferenceSolid)
    {
        string doc = withReferenceSolid
            ? await BoundaryPartAsync("curved_boundary_with_solid")
            : await NewPartAsync("curved_boundary_without_solid");
        Task<EntityRef> BowAsync(double y) => CurveAsync(doc, new SketchEntity
        {
            Kind = "spline", Points = [[0, y, 0], [5, y, 3], [10, y, 5], [15, y, 3], [20, y, 0]],
        });
        var bottom = await BowAsync(0);
        var top = await BowAsync(30);
        var left = await LineAsync(doc, [0, 0, 0], [0, 30, 0]);
        var right = await LineAsync(doc, [20, 0, 0], [20, 30, 0]);
        var source = await new SketchEditService(S).InfoAsync(doc, bottom.Name);
        double expectedArea = 30 * Assert.Single(source.Segments).LengthMm!.Value;

        var result = await new AdvancedFeatureService(S).SurfaceBoundaryAsync(doc, [bottom, top], [left, right]);

        Assert.Equal("NetBlend", result.Feature.Type);
        Assert.Equal(withReferenceSolid ? 2 : 1, result.Model.Bodies);
        Assert.Equal(withReferenceSolid ? 1000 : 0, result.Model.VolumeMm3, 1e-3);
        var sheet = Assert.Single(result.Bodies!);
        Assert.Equal("surface", sheet.Type);
        Assert.InRange(sheet.AreaMm2, expectedArea - 1, expectedArea + 1);
        var crown = await new InspectService(S).FaceListAsync(doc,
            new EntityFilter { Body = sheet.Id, Near = [10, 15, 5], WithinMm = 0.02 }, 10, 0);
        Assert.NotEmpty(crown.Faces);
    }

    private async Task<string> BoundaryPartAsync(string name)
    {
        string doc = await NewPartAsync(name);
        var sketches = new SketchService(S);
        await sketches.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sketches.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [60, 0], WidthMm = 10, HeightMm = 10 }]);
        await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 10 });
        return doc;
    }

    private Task<EntityRef> LineAsync(string doc, double[] from, double[] to) =>
        CurveAsync(doc, new SketchEntity { Kind = "line", From = from, To = to });

    private async Task<EntityRef> CurveAsync(string doc, SketchEntity curve)
    {
        var sketches = new SketchService(S);
        var started = await sketches.StartAsync(doc, null, true);
        await sketches.AddAsync(doc, [curve]);
        await sketches.FinishAsync(doc);
        return new EntityRef { Name = started.Sketch };
    }
}
