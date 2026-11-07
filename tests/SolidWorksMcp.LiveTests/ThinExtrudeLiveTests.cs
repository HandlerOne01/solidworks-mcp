using Microsoft.Extensions.Hosting;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;
using Xunit;

namespace SolidWorksMcp.LiveTests;

public class ThinExtrudeContractTests
{
    [Fact]
    public void The_extrude_tool_exposes_an_optional_wall_thickness()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new());
        ServerSetup.Configure(builder, fileLog: false);
        using var host = builder.Build();

        var tool = Assert.Single(ServerSetup.ListedTools(host.Services), item => item.Name == "feature_extrude");
        Assert.True(tool.InputSchema.GetProperty("properties").TryGetProperty("thinThicknessMm", out _));
        if (tool.InputSchema.TryGetProperty("required", out var required))
            Assert.DoesNotContain(required.EnumerateArray(), item => item.GetString() == "thinThicknessMm");
    }

    [Theory]
    [InlineData(0, false, "blind", null)]
    [InlineData(-1, false, "blind", null)]
    [InlineData(double.NaN, false, "blind", null)]
    [InlineData(double.PositiveInfinity, false, "blind", null)]
    [InlineData(2, false, "through_all", null)]
    [InlineData(2, true, "through_all", null)]
    [InlineData(2, false, "mid_plane", null)]
    [InlineData(2, false, "blind", 5.0)]
    [InlineData(2, true, "blind", 5.0)]
    public void Invalid_or_unsupported_thin_wall_requests_are_rejected(double thickness, bool cut, string end, double? draft)
    {
        var request = new ExtrudeRequest { ThinThicknessMm = thickness, Cut = cut, End = end, DraftDeg = draft };
        Assert.Equal(ErrorCodes.InvalidArgument,
            Assert.Throws<SwException>(() => FeatureService.ThinThicknessM(request, end)).Code);
    }

    [Fact]
    public void A_blind_thin_cut_accepts_total_thickness_in_millimetres()
    {
        var request = new ExtrudeRequest { ThinThicknessMm = 3, Cut = true, End = "blind" };
        Assert.Equal(0.003, FeatureService.ThinThicknessM(request, "blind")!.Value, 1e-12);
    }
}

public class ThinExtrudeLiveTests(LiveFixture fx) : LiveTest(fx)
{
    [Fact]
    public async Task An_open_line_extrudes_into_a_wall_centered_on_the_profile()
    {
        string doc = await NewPartAsync("thin_open_wall");
        var sketch = new SketchService(S);
        await sketch.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sketch.AddAsync(doc, [new SketchEntity { Kind = "line", From = [0, 0], To = [20, 0] }]);

        var result = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest
        {
            Doc = doc, DepthMm = 30, ThinThicknessMm = 2,
        });

        Assert.Equal(1, result.Model.Bodies);
        Assert.Equal(1200, result.Model.VolumeMm3, 1e-3);
        var box = Assert.IsType<BoxMm>(result.Model.Box);
        Assert.Equal(30, box.Max[1] - box.Min[1], 1e-3);
        Assert.Equal(-1, box.Min[2], 1e-3);
        Assert.Equal(1, box.Max[2], 1e-3);
    }

    [Fact]
    public async Task A_closed_circle_extrudes_into_an_uncapped_tube()
    {
        string doc = await NewPartAsync("thin_closed_tube");
        var sketch = new SketchService(S);
        await sketch.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sketch.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [0, 0], RadiusMm = 10 }]);

        var result = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest
        {
            Doc = doc, DepthMm = 30, ThinThicknessMm = 2,
        });

        Assert.Equal(1, result.Model.Bodies);
        Assert.Equal(1200 * Math.PI, result.Model.VolumeMm3, 1e-3);
    }

    [Fact]
    public async Task An_open_line_cuts_a_centered_blind_groove()
    {
        string doc = await BlockWithOpenLineAsync("thin_blind_cut");

        var result = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest
        {
            Doc = doc, Cut = true, DepthMm = 5, ThinThicknessMm = 3,
        });

        Assert.Equal("removed", result.Verified.Material);
        Assert.Equal(-300, result.Verified.VolumeDeltaMm3, 1e-3); // 20 mm long × 3 mm wide × 5 mm deep
        Assert.Equal(71700, result.Model.VolumeMm3, 1e-3);
        Assert.Equal(1, result.Model.Bodies);
        Assert.Equal([0, -1, 0], result.DirectionModel!);
        Assert.Contains(result.NewFaces!, f => f.Surface == "plane" && Math.Abs(f.PickMm[1] - 15) < 1e-3 && Math.Abs(f.AreaMm2 - 60) < 1e-3);
        Assert.Contains(result.NewFaces!, f => f.Surface == "plane" && Math.Abs(f.PickMm[2] + 1.5) < 1e-3 && Math.Abs(f.AreaMm2 - 100) < 1e-3);
        Assert.Contains(result.NewFaces!, f => f.Surface == "plane" && Math.Abs(f.PickMm[2] - 1.5) < 1e-3 && Math.Abs(f.AreaMm2 - 100) < 1e-3);
    }

    [Fact]
    public async Task A_closed_circle_cuts_a_centered_blind_ring_groove()
    {
        string doc = await BlockWithTopProfileAsync("thin_ring_cut",
            new SketchEntity { Kind = "circle", Center = [0, 20, 0], RadiusMm = 10 });

        var result = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest
        {
            Doc = doc, Cut = true, DepthMm = 3, ThinThicknessMm = 2,
        });

        const double floorArea = 40 * Math.PI; // Annulus: π × (11² − 9²) mm².
        Assert.Equal("CutThin", result.Feature.Type);
        Assert.Equal(-3 * floorArea, result.Verified.VolumeDeltaMm3, 1e-3);
        Assert.Equal(1, result.Model.Bodies);
        Assert.Contains(result.NewFaces!, f => f.Surface == "plane" && Math.Abs(f.PickMm[1] - 17) < 1e-3 &&
            Math.Abs(f.AreaMm2 - floorArea) < 1e-3);
    }

    [Fact]
    public async Task A_thin_cut_pointing_away_preserves_the_input_sketch_and_body()
    {
        string doc = await BlockWithOpenLineAsync("thin_cut_away");
        var sketch = await new SketchService(S).FinishAsync(doc);
        var inspect = new InspectService(S);
        int features = (await inspect.FeatureListAsync(doc, false, 100, 0)).Total;

        var error = await Assert.ThrowsAsync<SwException>(() => new FeatureService(S).ExtrudeAsync(new ExtrudeRequest
        {
            Doc = doc, Sketch = sketch.Sketch, Cut = true, DepthMm = 5, ThinThicknessMm = 3, Direction = "+y",
        }));

        Assert.Contains(error.Code, new[] { ErrorCodes.FeatureFailed, ErrorCodes.RebuildError, ErrorCodes.VerifyFailed });
        Assert.Equal(features, (await inspect.FeatureListAsync(doc, false, 100, 0)).Total);
        Assert.Equal(72000, (await inspect.ModelInfoAsync(doc)).VolumeMm3!.Value, 1e-3);
        var result = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest
        {
            Doc = doc, Sketch = sketch.Sketch, Cut = true, DepthMm = 5, ThinThicknessMm = 3,
        });
        Assert.Equal(-300, result.Verified.VolumeDeltaMm3, 1e-3);
    }

    private Task<string> BlockWithOpenLineAsync(string name) => BlockWithTopProfileAsync(name,
        new SketchEntity { Kind = "line", From = [-10, 20, 0], To = [10, 20, 0] });

    private async Task<string> BlockWithTopProfileAsync(string name, SketchEntity profile)
    {
        string doc = await NewPartAsync(name);
        var sketch = new SketchService(S);
        await sketch.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sketch.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 60, HeightMm = 60 }]);
        await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        await sketch.StartAsync(doc, new EntityRef { At = [0, 20, 0] }, false);
        await sketch.AddAsync(doc, [profile]);
        return doc;
    }
}
