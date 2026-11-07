using SolidWorksMcp.Server.Hosting;
using Xunit;

namespace SolidWorksMcp.LiveTests;

public class SurfaceSweepContractTests
{
    [Fact]
    public void Surface_sweep_is_registered_as_a_body_tool() =>
        Assert.Equal(("bodies", false), Toolsets.Catalog["surface_sweep"]);
}
