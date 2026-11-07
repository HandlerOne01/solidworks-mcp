using SolidWorksMcp.Core;
using Xunit;

namespace SolidWorksMcp.UnitTests;

public class SwOptionsTests
{
    [Fact]
    public void Invalid_process_setting_is_rejected_instead_of_falling_back_to_the_active_instance()
    {
        const string name = "SWMCP_SOLIDWORKS_PID";
        string? previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "not-a-pid");
            var error = Assert.Throws<SwException>(SwOptions.FromEnvironment);
            Assert.Equal(ErrorCodes.InvalidArgument, error.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
