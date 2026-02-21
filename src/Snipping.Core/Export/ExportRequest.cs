namespace Snipping.Core.Export;

public sealed record ExportRequest(
    byte[] ImageData,
    ExportFormat Format,
    int JpegQuality = 90);
