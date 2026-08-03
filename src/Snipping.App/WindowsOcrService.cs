using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using Snipping.Core.Ocr;
using CoreOcrLine = Snipping.Core.Ocr.OcrTextLine;
using CoreOcrResult = Snipping.Core.Ocr.OcrResult;

namespace Snipping.App;

/// <summary>
/// Windows-provided OCR adapter. This API requires package identity at runtime;
/// the MSIX build is the supported distribution for this service.
/// </summary>
public sealed class WindowsOcrService : IOcrService
{
    private readonly OcrEngine? _engine;

    public WindowsOcrService()
    {
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (_engine is null)
                UnavailableReason = "未找到可用的 Windows OCR 语言包，请在系统设置中安装语言包。";
        }
        catch (Exception ex)
        {
            UnavailableReason = $"Windows OCR 初始化失败：{ex.Message}";
        }
    }

    public bool IsAvailable => _engine is not null;

    public string? UnavailableReason { get; }

    public async Task<CoreOcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
    {
        if (_engine is null)
            return new CoreOcrResult(Array.Empty<CoreOcrLine>(), UnavailableReason);

        try
        {
            ValidateImage(image);
            using var bitmap = CreateSoftwareBitmap(image);
            var nativeResult = await _engine.RecognizeAsync(bitmap).AsTask(cancellationToken);

            var lines = OcrLineGeometry.OrderLines(nativeResult.Lines.Select(CreateLine));

            return new CoreOcrResult(lines);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CoreOcrResult(Array.Empty<CoreOcrLine>(), $"OCR 识别失败：{ex.Message}");
        }
    }

    private static CoreOcrLine CreateLine(OcrLine line)
    {
        var words = line.Words.Select(static word => new OcrWordBox(
            (float)word.BoundingRect.X,
            (float)word.BoundingRect.Y,
            (float)word.BoundingRect.Width,
            (float)word.BoundingRect.Height));
        return OcrLineGeometry.MergeWords(line.Text ?? string.Empty, words);
    }

    private static SoftwareBitmap CreateSoftwareBitmap(OcrImage image)
    {
        var packed = new byte[checked(image.Width * image.Height * 4)];
        var source = image.Pixels.Span;
        var sourceStride = image.Stride;
        var rowLength = checked(image.Width * 4);

        for (var row = 0; row < image.Height; row++)
        {
            var sourceOffset = checked(row * sourceStride);
            var targetOffset = checked(row * rowLength);
            source.Slice(sourceOffset, rowLength).CopyTo(packed.AsSpan(targetOffset, rowLength));
        }

        var softwareBitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            image.Width,
            image.Height,
            BitmapAlphaMode.Premultiplied);

        try
        {
            softwareBitmap.CopyFromBuffer(CryptographicBuffer.CreateFromByteArray(packed));
            return softwareBitmap;
        }
        catch
        {
            softwareBitmap.Dispose();
            throw;
        }
    }

    private static void ValidateImage(OcrImage image)
    {
        if (image.Width <= 0 || image.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(image), "OCR 图像尺寸必须大于零。");
        if (image.Stride < image.Width * 4)
            throw new ArgumentException("OCR 图像 stride 小于 BGRA32 行宽。", nameof(image));
        if (image.Pixels.Length < image.RequiredLength)
            throw new ArgumentException("OCR 图像缓冲区长度不足。", nameof(image));
    }
}
