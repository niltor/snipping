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
        var request = new ExportRequest([137, 80, 78, 71], ExportFormat.Png);

        try
        {
            var path = await manager.ExportAsync(dir, "snip", request, now);

            Assert.EndsWith(".png", path, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(path));
            Assert.Equal([137, 80, 78, 71], await File.ReadAllBytesAsync(path));
            Assert.Contains("20260101_010203_000", path);
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
