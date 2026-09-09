using NowPlaying.Device;

namespace NowPlaying.Windows.Tests;

/// <summary>
/// Tests that need the Windows implementations, and so cannot live in the
/// portable suite. They exist because a portable test project silently
/// resolves the neutral target on Windows too, which would leave the Windows
/// services untested on both platforms (macos-port-plan M0 exit test).
/// </summary>
public class WindowsDeviceTests
{
    [Fact]
    public void DeviceEnumerationDoesNotThrow()
    {
        // Reads the PnP tree only; count depends on what is plugged in.
        var paths = StreamDeckHid.FindDevicePaths();
        Assert.All(paths, p => Assert.Contains("vid_0fd9&pid_0084", p, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BeforeBindingNothingResolvesAndNothingNeighbours()
    {
        using var service = new DisplayBrightnessService();
        Assert.Null(service.Resolve(null));
        Assert.Null(service.Resolve("Odyssey G95NC"));
        Assert.Null(service.Neighbor(null, 1));
        Assert.Equal(DisplayBrightnessSnapshot.Unavailable, service.Get(null));
        Assert.Equal("DELL U2723QE not connected", service.Get("DELL U2723QE").Name);
        Assert.False(service.Adjust(null, 2));
        Assert.False(service.Toggle("anything"));
    }

    [Fact]
    public void MonitorEnumerationDoesNotThrowAndNamesAreNonEmpty()
    {
        var monitors = MonitorConfiguration.Enumerate();
        try
        {
            Assert.All(monitors, m => Assert.False(string.IsNullOrWhiteSpace(m.Name)));
        }
        finally
        {
            MonitorConfiguration.Destroy(monitors);
        }
    }
}
