using System.Runtime.InteropServices;
using SolidWorksMcp.Core.Com;
using Xunit;

namespace SolidWorksMcp.UnitTests;

public class ComConnectionTests
{
    [Theory]
    [InlineData(0x800706B5u, true)]
    [InlineData(0x80010108u, true)]
    [InlineData(0x80010105u, false)]
    [InlineData(0x80010001u, false)]
    public void Only_an_unavailable_application_proxy_triggers_reconnection(uint hresult, bool disconnected)
    {
        var error = new COMException("Test RPC result", unchecked((int)hresult));
        Assert.Equal(disconnected, ComArgs.IsApplicationDisconnect(error));
        if (hresult == 0x800706B5u)
            Assert.False(ComArgs.IsDisconnect(error));
    }
}
