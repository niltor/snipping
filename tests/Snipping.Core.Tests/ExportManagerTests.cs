using Snipping.Core.Export;

namespace Snipping.Core.Tests;

public sealed class ExportManagerTests
{
    [Fact]
    public async Task ExportAsync_WritesFileWithExpectedExtension()
    {
        var manager = new ExportManager();
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var now = new DateTimeOffset(2026, 1, 1, 1, 2, 3, TimeSpan.Zero);
        var request = new ExportRequest([1, 2, 3], ExportFormat.Png);

        var path = await manager.ExportAsync(dir, "snip", request, now);

        Assert.EndsWith(".png", path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public void BuildClipboardPayload_IncludesMimePrefix()
    {
        var manager = new ExportManager();

        var payload = manager.BuildClipboardPayload([7, 8], ExportFormat.Jpeg);

        Assert.StartsWith("image/jpeg|", System.Text.Encoding.UTF8.GetString(payload));
    }
}
