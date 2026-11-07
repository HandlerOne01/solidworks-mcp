using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

public class BodyScaleLiveTests(LiveFixture fx) : LiveTest(fx)
{
    [Fact]
    public async Task Scales_only_the_requested_solid_about_the_model_origin()
    {
        string doc = await NewPartAsync("scale_solid");
        await CubeAsync(doc, 20);
        await CubeAsync(doc, 90);
        var measure = new MeasureService(S);
        var bodies = (await measure.BodyListAsync(doc)).Bodies.OrderBy(b => b.Box!.Min[0]).ToArray();

        var result = await new BodyScaleService(S).ScaleAsync(doc, [new EntityRef { Id = bodies[0].Id }], 2);

        Assert.Equal("Scale", result.Feature.Type);
        Assert.Equal(2, result.Model.Bodies);
        Assert.Equal(7000, result.Verified.VolumeDeltaMm3, 0.001);
        var scaled = Assert.Single(result.Bodies!);
        Assert.Equal("solid", scaled.Type);
        Assert.Equal(8000, scaled.VolumeMm3!.Value, 0.001);
        Assert.Equal(2400, scaled.AreaMm2, 0.001);
        AssertBox(scaled.Box, [30, 0, -10], [50, 20, 10]);

        var unscaled = Assert.Single((await measure.BodyListAsync(doc)).Bodies, b => b.Name == bodies[1].Name);
        Assert.Equal(1000, unscaled.VolumeMm3!.Value, 0.001);
        AssertBox(unscaled.Box, [85, 0, -5], [95, 10, 5]);
    }

    [Fact]
    public async Task Scales_the_requested_surface_and_leaves_a_solid_untouched()
    {
        string doc = await NewPartAsync("scale_surface");
        await CubeAsync(doc, 90);
        var sketches = new SketchService(S);
        await sketches.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sketches.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [40, 0], WidthMm = 50, HeightMm = 30 }]);
        var extruded = await new SurfaceService(S).ExtrudeAsync(new SurfaceExtrudeRequest { Doc = doc, DepthMm = 20 });
        var surface = Assert.Single(extruded.Bodies!);

        var result = await new BodyScaleService(S).ScaleAsync(doc, [new EntityRef { Id = surface.Id }], 0.5);

        Assert.Equal("Scale", result.Feature.Type);
        Assert.Equal(2, result.Model.Bodies);
        Assert.Equal(1000, result.Model.VolumeMm3, 0.001);
        var scaled = Assert.Single(result.Bodies!);
        Assert.Equal("surface", scaled.Type);
        Assert.Equal(800, scaled.AreaMm2, 0.02);
        AssertBox(scaled.Box, [7.5, 0, -7.5], [32.5, 10, 7.5]);
    }

    [Fact]
    public async Task A_missing_body_is_rejected_without_adding_a_feature()
    {
        string doc = await NewPartAsync("scale_missing_body");
        await CubeAsync(doc, 20);
        var inspect = new InspectService(S);
        int features = (await inspect.FeatureListAsync(doc, false, 100, 0)).Total;

        var error = await Assert.ThrowsAsync<SwException>(() =>
            new BodyScaleService(S).ScaleAsync(doc, [new EntityRef { Name = "missing scale input" }], 2));

        Assert.Equal(ErrorCodes.EntityNotFound, error.Code);
        Assert.Equal(features, (await inspect.FeatureListAsync(doc, false, 100, 0)).Total);
    }

    private async Task CubeAsync(string doc, double x)
    {
        var sketches = new SketchService(S);
        await sketches.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sketches.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [x, 0], WidthMm = 10, HeightMm = 10 }]);
        await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 10, Merge = false });
    }

    private static void AssertBox(BoxMm? actual, double[] min, double[] max)
    {
        Assert.NotNull(actual);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(min[i], actual.Min[i], 0.01);
            Assert.Equal(max[i], actual.Max[i], 0.01);
        }
    }
}
