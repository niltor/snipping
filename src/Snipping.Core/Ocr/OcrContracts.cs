namespace Snipping.Core.Ocr;

/// <summary>
/// A tightly packed BGRA image passed to an OCR provider.
/// </summary>
public sealed record OcrImage(ReadOnlyMemory<byte> Pixels, int Width, int Height, int Stride)
{
    public int RequiredLength => checked(Stride * Height);
}

public sealed record OcrTextLine(string Text, int X, int Y, int Width, int Height)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Text) && Width > 0 && Height > 0;
}

public sealed record OcrResult(IReadOnlyList<OcrTextLine> Lines, string? ErrorMessage = null)
{
    public bool IsSuccess => ErrorMessage is null;

    public static OcrResult Empty { get; } = new(Array.Empty<OcrTextLine>());
}

public interface IOcrService
{
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    Task<OcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default);
}
