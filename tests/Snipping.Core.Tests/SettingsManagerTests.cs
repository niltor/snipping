using Snipping.Core.Settings;

namespace Snipping.Core.Tests;

public sealed class SettingsManagerTests
{
    [Fact]
    public async Task LoadAsync_ReturnsDefault_WhenFileDoesNotExist()
    {
        var manager = new SettingsManager();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");

        var settings = await manager.LoadAsync(path);

        Assert.Equal("Ctrl+Shift+S", settings.Hotkey);
        Assert.Equal(90, settings.JpegQuality);
    }

    [Fact]
    public async Task SaveAndLoadAsync_PersistsSettings()
    {
        var manager = new SettingsManager();
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        var expected = new SnippingSettings
        {
            Hotkey = "Ctrl+Alt+A",
            JpegQuality = 80,
            FileNamePrefix = "capture"
        };

        await manager.SaveAsync(path, expected);
        var actual = await manager.LoadAsync(path);

        Assert.Equal(expected.Hotkey, actual.Hotkey);
        Assert.Equal(expected.JpegQuality, actual.JpegQuality);
        Assert.Equal(expected.FileNamePrefix, actual.FileNamePrefix);
    }
}
