using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

public class SurfaceSweepLiveTests(LiveFixture fx) : LiveTest(fx)
{
    [Fact]
    public async Task Open_profile_and_normal_path_create_a_single_rectangular_surface()
    {
        string doc = await NewPartAsync("swept_sheet");
        var sketch = new SketchService(S);
        var profile = await sketch.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sketch.AddAsync(doc, [new SketchEntity { Kind = "line", From = [0, 0], To = [20, 0] }]);
        await sketch.FinishAsync(doc);

        async Task<EntityRef> PathAsync(double startY)
        {
            var path = await sketch.StartAsync(doc, null, true);
            await sketch.AddAsync(doc, [new SketchEntity { Kind = "line", From = [0, startY, 0], To = [0, startY + 30, 0] }]);
            await sketch.FinishAsync(doc);
            return new EntityRef { Name = path.Sketch };
        }

        var service = new AdvancedFeatureService(S);
        var profileRef = new EntityRef { Name = profile.Sketch };
        var detached = await PathAsync(40);
        var error = await Assert.ThrowsAsync<SwException>(() => service.SurfaceSweepAsync(doc, profileRef, detached));
        Assert.Equal(ErrorCodes.InvalidArgument, error.Code);

        var pathRef = await PathAsync(0);
        var result = await service.SurfaceSweepAsync(doc, profileRef, pathRef);
        Assert.Equal("SweepRefSurface", result.Feature.Type);
        Assert.Equal(1, result.Model.Bodies);
        Assert.Equal(0, result.Model.VolumeMm3);
        Assert.Equal(600, Assert.Single(result.Bodies!).AreaMm2, 1e-3);
    }

    [Fact]
    public async Task A_line_swept_along_a_quarter_circle_makes_a_cylindrical_sheet()
    {
        string doc = await NewPartAsync("swept_arc");
        var sketch = new SketchService(S);
        var profile = await sketch.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sketch.AddAsync(doc, [new SketchEntity { Kind = "line", From = [0, 0], To = [20, 0] }]);
        await sketch.FinishAsync(doc);

        var path = await sketch.StartAsync(doc, null, true);
        double mid = 20 / Math.Sqrt(2);
        await sketch.AddAsync(doc, [new SketchEntity { Kind = "arc", Start = [0, 0, 0], Mid = [0, mid, 20 - mid], End = [0, 20, 20] }]);
        await sketch.FinishAsync(doc);

        var result = await new AdvancedFeatureService(S).SurfaceSweepAsync(doc,
            new EntityRef { Name = profile.Sketch }, new EntityRef { Name = path.Sketch });
        Assert.Equal("SweepRefSurface", result.Feature.Type);
        Assert.Equal(200 * Math.PI, Assert.Single(result.Bodies!).AreaMm2, 1e-3);
    }
}
