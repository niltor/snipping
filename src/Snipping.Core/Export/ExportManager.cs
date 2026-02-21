namespace Snipping.Core.Export;

public sealed class ExportManager
{
    public string BuildFileName(string filePrefix, ExportFormat format, DateTimeOffset now)
    {
        var ext = format == ExportFormat.Png ? "png" : "jpg";
        return $"{filePrefix}_{now:yyyyMMdd_HHmmss_fff}.{ext}";
    }

    public async Task<string> ExportAsync(string directory, string filePrefix, ExportRequest request, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, BuildFileName(filePrefix, request.Format, now));
        await File.WriteAllBytesAsync(path, request.ImageData, cancellationToken);
        return path;
    }
}
