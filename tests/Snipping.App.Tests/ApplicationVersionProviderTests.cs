namespace Snipping.App.Tests;

public sealed class ApplicationVersionProviderTests
{
    [Fact]
    public void DisplayVersionUsesTheFourPartPackageFormat()
    {
        var version = ApplicationVersionProvider.GetDisplayVersion();

        Assert.Equal("1.0.13.0", version);
        Assert.Equal(4, version.Split('.').Length);
        Assert.All(version.Split('.'), part => Assert.True(int.TryParse(part, out _)));
    }
}
