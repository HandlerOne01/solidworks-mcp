using Microsoft.Extensions.Hosting;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;
using Xunit;

namespace SolidWorksMcp.LiveTests;

public class BodyMirrorContractTests
{
    [Fact]
    public void Body_mirror_requires_explicit_bodies_and_a_plane()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new());
        ServerSetup.Configure(builder, fileLog: false);
        using var host = builder.Build();

        var tool = Assert.Single(ServerSetup.ListedTools(host.Services), t => t.Name == "body_mirror");
        var required = tool.InputSchema.GetProperty("required").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.Contains("bodies", required);
        Assert.Contains("plane", required);
        Assert.False(tool.Annotations!.ReadOnlyHint);
    }

    [Fact]
    public void Oblique_mirror_measures_the_body_along_reflected_axis_directions()
    {
        Vec3[] vertices = [new(0.01, 0.01, 0.002), new(0.03, 0.01, 0.002), new(0.01, 0.04, 0.002),
            new(0.01, 0.01, 0.012), new(0.03, 0.01, 0.012), new(0.01, 0.04, 0.012)];

        var bounds = BodyMirrorGeometry.Bounds(direction => vertices.MaxBy(v => v.Dot(direction)),
            (new Vec3(0.005, 0, 0), new Vec3(0.6, 0.8, 0)));

        double[] expected = [-0.032, -0.0268, 0.002, 0.0024, -0.0076, 0.012];
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], bounds[i], 1e-12);
    }
}
