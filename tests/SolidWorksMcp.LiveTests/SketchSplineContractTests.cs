using Microsoft.Extensions.Hosting;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;
using Xunit;

namespace SolidWorksMcp.LiveTests;

public class SketchSplineContractTests
{
    [Fact]
    public void Catalog_exposes_an_indexed_fit_point_edit()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new());
        ServerSetup.Configure(builder, fileLog: false);
        using var host = builder.Build();
        var tool = Assert.Single(ServerSetup.ListedTools(host.Services), t => t.Name == "sketch_spline_edit");
        var schema = tool.InputSchema;
        var required = schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("spline", required);
        Assert.Contains("moves", required);
        var move = schema.GetProperty("properties").GetProperty("moves").GetProperty("items");
        Assert.True(move.GetProperty("properties").TryGetProperty("index", out _));
        Assert.True(move.GetProperty("properties").TryGetProperty("atMm", out _));
    }

    [Fact]
    public void Duplicate_indexes_are_refused_before_opening_a_sketch()
    {
        var service = new SketchEditService(null!);
        var error = Assert.Throws<SwException>(() =>
        {
            _ = service.SplineEditAsync(null, new EntityRef { Name = "Spline1" },
                [new() { Index = 0, AtMm = [1, 2] }, new() { Index = 0, AtMm = [3, 4] }]);
        });
        Assert.Equal(ErrorCodes.InvalidArgument, error.Code);
    }
}
