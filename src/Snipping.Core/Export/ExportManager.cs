using System.Text;

namespace Snipping.Core.Export;

public sealed class ExportManager
{
    public string BuildFileName(string filePrefix, ExportFormat format, DateTimeOffset now)
    {
        var ext = format == ExportFormat.Png ? "png" : "jpg";
        return $"{filePrefix}_{now:yyyyMMdd_HHmmss}.{ext}";
    }

    public async Task<string> ExportAsync(string directory, string filePrefix, ExportRequest request, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, BuildFileName(filePrefix, request.Format, now));
        await File.WriteAllBytesAsync(path, request.ImageData, cancellationToken);
        return path;
    }

    public byte[] BuildClipboardPayload(byte[] imageData, ExportFormat format)
    {
        var header = format == ExportFormat.Png ? "image/png" : "image/jpeg";
        var prefix = Encoding.UTF8.GetBytes($"{header}|");
        var payload = new byte[prefix.Length + imageData.Length];
        Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
        Buffer.BlockCopy(imageData, 0, payload, prefix.Length, imageData.Length);
        return payload;
    }
}
