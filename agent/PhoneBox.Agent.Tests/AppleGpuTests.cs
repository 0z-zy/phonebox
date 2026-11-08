using PhoneBox.Agent;
using Xunit;

/// <summary>The macOS GPU comes from ioreg text (no nvidia-smi there): load, memory the GPU holds, the machine's RAM as total.</summary>
public sealed class AppleGpuTests
{
    private const double SixteenGb = 16.0 * 1024 * 1024 * 1024;

    [Fact]
    public void LoadAndMemoryAreReadFromTheAcceleratorEntry()
    {
        var g = SystemMonitor.ParseAppleGpu("""
            +-o AGXAcceleratorG14X  <class AGXAcceleratorG14X>
              {
                "model" = "Apple M2 Pro"
                "PerformanceStatistics" = {"Device Utilization %"=37,"Alloc system memory"=2147483648,"Renderer Utilization %"=30}
              }
            """, SixteenGb);
        Assert.NotNull(g);
        Assert.Equal("Apple M2 Pro", g!.Name);
        Assert.Equal(37, g.Util);
        Assert.Equal(2.0, g.VramUsed);
        Assert.Equal(16.0, g.VramTotal);
        Assert.Null(g.Temp);   // no sensors: the panel must not show 0 °C
        Assert.Null(g.Power);
    }

    [Fact]
    public void SpacesAroundTheEqualsSignAreFine()
    {
        var g = SystemMonitor.ParseAppleGpu("\"Device Utilization %\" = 5\n\"Alloc system memory\" = 1073741824", SixteenGb);
        Assert.Equal(5, g!.Util);
        Assert.Equal(1.0, g.VramUsed);
        Assert.Equal("Apple GPU", g.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nothing useful here")]
    public void WithoutAnAcceleratorThereIsNoGpu(string text) => Assert.Null(SystemMonitor.ParseAppleGpu(text, SixteenGb));

    [Fact]
    public void WithoutAMemoryFigureTheMemoryStaysEmpty()
    {
        var g = SystemMonitor.ParseAppleGpu("\"Device Utilization %\"=9", 0);
        Assert.Equal(9, g!.Util);
        Assert.Null(g.VramUsed);
        Assert.Null(g.VramTotal);
    }
}
