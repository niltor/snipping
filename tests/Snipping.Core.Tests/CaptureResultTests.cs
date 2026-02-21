using Snipping.Core.Capture;

namespace Snipping.Core.Tests;

public sealed class CaptureResultTests
{
    [Fact]
    public void Ctor_AssignsFields()
    {
        var result = new CaptureResult([1, 2], "image/png", 96, 96, "display-1");

        Assert.Equal("image/png", result.ImageFormat);
        Assert.Equal(96, result.DpiX);
        Assert.Equal(96, result.DpiY);
        Assert.Equal("display-1", result.DisplayId);
        Assert.Equal([1, 2], result.ImageData);
    }
}
