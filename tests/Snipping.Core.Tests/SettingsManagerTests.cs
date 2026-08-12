using Snipping.Core.Settings;
using Snipping.Core.Ocr;

namespace Snipping.Core.Tests;

public sealed class SettingsManagerTests
{
    [Fact]
    public async Task LoadAsync_ReturnsDefault_WhenFileDoesNotExist()
    {
        var manager = new SettingsManager();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.ini");

        var settings = await manager.LoadAsync(path);

        Assert.Equal("Ctrl+Shift+S", settings.Hotkey);
        Assert.Equal(90, settings.JpegQuality);
        Assert.Equal(90, settings.PinOpacity);
    }

    [Fact]
    public async Task SaveAndLoadAsync_PersistsSettings()
    {
        var manager = new SettingsManager();
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.ini");
        var expected = new SnippingSettings
        {
            Hotkey = "Ctrl+Alt+A",
            JpegQuality = 80,
            PinOpacity = 75,
            FileNamePrefix = "capture",
            OcrPreferredLanguage = "ja-JP",
            OcrBackend = OcrBackend.WindowsAi,
            StartWithWindows = true
        };

        try
        {
            await manager.SaveAsync(path, expected);
            var actual = await manager.LoadAsync(path);

            Assert.Equal(expected.Hotkey, actual.Hotkey);
            Assert.Equal(expected.JpegQuality, actual.JpegQuality);
            Assert.Equal(expected.PinOpacity, actual.PinOpacity);
            Assert.Equal(expected.FileNamePrefix, actual.FileNamePrefix);
            Assert.Equal(expected.OcrPreferredLanguage, actual.OcrPreferredLanguage);
            Assert.Equal(expected.OcrBackend, actual.OcrBackend);
            Assert.Equal(expected.StartWithWindows, actual.StartWithWindows);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LoadAsync_ReturnsDefault_WhenValuesAreInvalid()
    {
        var manager = new SettingsManager();
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.ini");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, "JpegQuality=not-a-number\nPinOpacity=200\nHotkey=\nTheme=Unknown");

        try
        {
            var settings = await manager.LoadAsync(path);
            Assert.Equal("Ctrl+Shift+S", settings.Hotkey);
            Assert.Equal(100, settings.PinOpacity);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
