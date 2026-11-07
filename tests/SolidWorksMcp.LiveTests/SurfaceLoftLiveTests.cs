using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;
using Xunit;

namespace SolidWorksMcp.LiveTests;

public class SurfaceLoftContractTests
{
    [Fact]
    public void Surface_loft_is_registered_as_a_body_tool() =>
        Assert.Equal(("bodies", false), Toolsets.Catalog["surface_loft"]);

    [Fact]
    public void Circular_profile_samples_stay_in_the_3d_sketch_plane()
    {
        var center = new Vec3(0.1, -0.2, 0.3);
        var normal = new Vec3(1, 1, 0).Normalized();
        const double radius = 0.05;
        var circle = AdvancedFeatureLogic.Seg.Circle(center, radius, normal);
        Assert.NotEmpty(circle.Samples);
        Assert.All(circle.Samples, point =>
        {
            Assert.Equal(0, (point - center).Dot(normal), 1e-12);
            Assert.Equal(radius, point.DistanceTo(center), 1e-12);
        });
    }
}

public class SurfaceLoftLiveTests(LiveFixture fx) : LiveTest(fx)
{
    [Fact]
    public async Task Loft_through_open_3d_sections_and_guide_creates_one_rectangular_sheet()
    {
        string doc = await NewPartAsync("surface_loft");
        var sketch = new SketchService(S);
        async Task<EntityRef> Line(double[] from, double[] to)
        {
            var started = await sketch.StartAsync(doc, null, true);
            await sketch.AddAsync(doc, [new SketchEntity { Kind = "line", From = from, To = to }]);
            await sketch.FinishAsync(doc);
            return new EntityRef { Name = started.Sketch };
        }

        var a = await Line([0, 0, 0], [20, 0, 0]);
        var b = await Line([0, 0, 30], [20, 0, 30]);
        var rail = await Line([0, 0, 0], [0, 0, 30]);
        var loft = new AdvancedFeatureService(S);
        var invalid = await Assert.ThrowsAsync<SwException>(() => loft.SurfaceLoftAsync(new SurfaceLoftRequest
        {
            Doc = doc, Profiles = [a],
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, invalid.Code);

        var result = await loft.SurfaceLoftAsync(new SurfaceLoftRequest
        {
            Doc = doc, Profiles = [a, b], Guides = [rail],
        });
        Assert.Equal("BlendRefSurface", result.Feature.Type);
        Assert.Equal(1, result.Model.Bodies);
        Assert.Equal(0, result.Model.VolumeMm3);
        Assert.Equal(600, Assert.Single(result.Bodies!).AreaMm2, 1e-3);
    }
}
