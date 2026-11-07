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
    [InlineData(2, true, "blind", null)]
    [InlineData(2, false, "through_all", null)]
    [InlineData(2, false, "mid_plane", null)]
    [InlineData(2, false, "blind", 5.0)]
    public void Invalid_or_unsupported_thin_wall_requests_are_rejected(double thickness, bool cut, string end, double? draft)
    {
        var request = new ExtrudeRequest { ThinThicknessMm = thickness, Cut = cut, End = end, DraftDeg = draft };
        Assert.Equal(ErrorCodes.InvalidArgument,
            Assert.Throws<SwException>(() => FeatureService.ThinThicknessM(request, end)).Code);
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
}
