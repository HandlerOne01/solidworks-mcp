using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

public class BodyMirrorLiveTests(LiveFixture fx) : LiveTest(fx)
{
    [Fact]
    public async Task Copies_two_selected_solids_and_preserves_all_three_originals()
    {
        string doc = await NewPartAsync("body_mirror_right");
        await ExtrudeAsync(doc, new SketchEntity
        {
            Kind = "polyline", Closed = true, Points = [[20, 0, 3], [35, 0, 3], [20, 0, 13]],
        }, 8);
        await CubeAsync(doc, 60);
        await CubeAsync(doc, 90);
        var measure = new MeasureService(S);
        var originals = (await measure.BodyListAsync(doc)).Bodies.OrderBy(b => b.Box!.Min[0]).ToArray();

        var result = await new BodyMirrorService(S).MirrorAsync(doc,
            [new EntityRef { Id = originals[0].Id }, new EntityRef { Id = originals[1].Id }], new EntityRef { Name = "right" });

        Assert.Equal("MirrorSolid", result.Feature.Type);
        Assert.Equal(5, result.Model.Bodies);
        Assert.Equal(2, result.Verified.BodyCountDelta);
        Assert.Equal(1600, result.Verified.VolumeDeltaMm3, 0.001);
        var copies = result.Bodies!.OrderBy(b => b.Box!.Min[0]).ToArray();
        Assert.Equal(2, copies.Length);
        Assert.Equal(1000, copies[0].VolumeMm3!.Value, 0.001);
        AssertBox(copies[0].Box, [-65, 0, -5], [-55, 10, 5]);
        Assert.Equal(600, copies[1].VolumeMm3!.Value, 0.001);
        AssertBox(copies[1].Box, [-35, 0, 3], [-20, 8, 13]);

        var after = (await measure.BodyListAsync(doc)).Bodies;
        foreach (var original in originals)
        {
            var same = Assert.Single(after, b => b.Name == original.Name);
            Assert.Equal(original.VolumeMm3!.Value, same.VolumeMm3!.Value, 0.001);
            AssertBox(same.Box, original.Box!.Min, original.Box.Max);
        }
    }

    [Fact]
    public async Task A_planar_face_can_define_an_offset_mirror_plane()
    {
        string doc = await NewPartAsync("body_mirror_offset");
        await CubeAsync(doc, 20);
        await CubeAsync(doc, 80);
        var bodies = (await new MeasureService(S).BodyListAsync(doc)).Bodies;
        var seed = Assert.Single(bodies, b => b.Box!.Min[0] > 50);

        var result = await new BodyMirrorService(S).MirrorAsync(doc, [new EntityRef { Id = seed.Id }],
            new EntityRef { Type = "face", At = [25, 5, 0] });

        AssertBox(Assert.Single(result.Bodies!).Box, [-35, 0, -5], [-25, 10, 5]);
    }

    [Fact]
    public async Task A_curved_face_is_rejected_before_any_mirror_feature_is_added()
    {
        string doc = await NewPartAsync("body_mirror_curved");
        await ExtrudeAsync(doc, new SketchEntity { Kind = "circle", Center = [60, 0, 0], RadiusMm = 5 }, 10);
        var seed = Assert.Single((await new MeasureService(S).BodyListAsync(doc)).Bodies);
        var inspect = new InspectService(S);
        int before = (await inspect.FeatureListAsync(doc, false, 100, 0)).Total;

        var error = await Assert.ThrowsAsync<SwException>(() => new BodyMirrorService(S).MirrorAsync(doc,
            [new EntityRef { Id = seed.Id }], new EntityRef { Type = "face", At = [65, 5, 0] }));

        Assert.Equal(ErrorCodes.InvalidArgument, error.Code);
        Assert.Equal(before, (await inspect.FeatureListAsync(doc, false, 100, 0)).Total);
    }

    private Task CubeAsync(string doc, double x) => ExtrudeAsync(doc,
        new SketchEntity { Kind = "rectangle", Center = [x, 0, 0], WidthMm = 10, HeightMm = 10 }, 10);

    private async Task ExtrudeAsync(string doc, SketchEntity profile, double depth)
    {
        var sketch = new SketchService(S);
        await sketch.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sketch.AddAsync(doc, [profile]);
        await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = depth, Merge = false });
    }

    private static void AssertBox(BoxMm? box, double[] min, double[] max)
    {
        Assert.NotNull(box);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(min[i], box.Min[i], 0.01);
            Assert.Equal(max[i], box.Max[i], 0.01);
        }
    }
}
