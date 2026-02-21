using System.Text.Json;

namespace Snipping.Core.Settings;

public sealed class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<SnippingSettings> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return new SnippingSettings();
        }

        await using var stream = File.OpenRead(filePath);
        var settings = await JsonSerializer.DeserializeAsync<SnippingSettings>(stream, JsonOptions, cancellationToken) ?? new SnippingSettings();
        Migrate(settings);
        return settings;
    }

    public async Task SaveAsync(string filePath, SnippingSettings settings, CancellationToken cancellationToken = default)
    {
        Migrate(settings);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
    }

    private static void Migrate(SnippingSettings settings)
    {
        if (settings.Version <= 0)
        {
            settings.Version = SnippingSettings.CurrentVersion;
        }

        settings.JpegQuality = Math.Clamp(settings.JpegQuality, 1, 100);
        if (string.IsNullOrWhiteSpace(settings.Hotkey))
        {
            settings.Hotkey = "Ctrl+Shift+S";
        }

        if (string.IsNullOrWhiteSpace(settings.SaveDirectory))
        {
            settings.SaveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Snipping");
        }

        if (string.IsNullOrWhiteSpace(settings.FileNamePrefix))
        {
            settings.FileNamePrefix = "snip";
        }
    }
}
