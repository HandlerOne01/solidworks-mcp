using Microsoft.Extensions.Hosting;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;
using Xunit;

namespace SolidWorksMcp.LiveTests;

public class BodyScaleContractTests
{
    [Fact]
    public void Body_scale_requires_explicit_bodies_and_a_factor()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new());
        ServerSetup.Configure(builder, fileLog: false);
        using var host = builder.Build();

        var tool = Assert.Single(ServerSetup.ListedTools(host.Services), t => t.Name == "body_scale");
        var required = tool.InputSchema.GetProperty("required").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.Contains("bodies", required);
        Assert.Contains("factor", required);
        Assert.False(tool.Annotations!.ReadOnlyHint);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_factors_are_rejected_before_dispatch(double factor)
    {
        var error = Assert.Throws<SwException>(() =>
            BodyScaleService.ValidateInputs([new EntityRef { Id = "B1" }], factor));

        Assert.Equal(ErrorCodes.InvalidArgument, error.Code);
    }

    [Fact]
    public void An_empty_body_list_cannot_mean_the_whole_part()
    {
        var error = Assert.Throws<SwException>(() => BodyScaleService.ValidateInputs([], 2));

        Assert.Equal(ErrorCodes.InvalidArgument, error.Code);
    }

    [Fact]
    public void Geometry_requires_cubic_volume_and_squared_area()
    {
        var original = new BodyScaleGeometry(true, 0.0006, 0.000001, [0.015, 0, -0.005, 0.025, 0.01, 0.005], 6, 12);
        var doubled = new BodyScaleGeometry(true, 0.0024, 0.000008, [0.03, 0, -0.01, 0.05, 0.02, 0.01], 6, 12);

        Assert.True(original.Matches(doubled, 2));
        Assert.False(original.Matches(doubled with { Volume = 0.000004 }, 2));
        Assert.False(original.Matches(doubled with { Area = 0.0012 }, 2));
    }

    [Fact]
    public void Surface_geometry_requires_origin_scaled_extents()
    {
        var original = new BodyScaleGeometry(false, 0.0032, 0, [0.015, 0, -0.015, 0.065, 0.02, 0.015], 4, 12);
        var halved = new BodyScaleGeometry(false, 0.0008, 0, [0.0075, 0, -0.0075, 0.0325, 0.01, 0.0075], 4, 12);

        Assert.True(original.Matches(halved, 0.5));
        Assert.False(original.Matches(halved with { Bounds = [0.0275, 0, -0.0075, 0.0525, 0.01, 0.0075] }, 0.5));
        Assert.False(original.Matches(halved, 1));
    }
}
