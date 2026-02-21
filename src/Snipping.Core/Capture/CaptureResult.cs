namespace Snipping.Core.Capture;

public sealed record CaptureResult(
    byte[] ImageData,
    string ImageFormat,
    double DpiX,
    double DpiY,
    string DisplayId);
