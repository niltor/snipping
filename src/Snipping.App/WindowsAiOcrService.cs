using Microsoft.Graphics.Imaging;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Imaging;
using Snipping.Core.Ocr;
using Snipping.Core.Settings;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using CoreOcrLine = Snipping.Core.Ocr.OcrTextLine;
using CoreOcrResult = Snipping.Core.Ocr.OcrResult;

namespace Snipping.App;

/// <summary>
/// Windows App SDK AI-backed OCR adapter. The API is available only when the
/// Windows AI text recognition model and compatible hardware are present.
/// </summary>
public sealed class WindowsAiOcrService : IOcrService
{
    public sealed record Availability(
        bool IsSupported,
        AIFeatureReadyState ReadyState,
        string? UnavailableReason)
    {
        public bool IsReady => ReadyState == AIFeatureReadyState.Ready;
    }

    private readonly string _uiLanguage;
    private readonly Availability _availability;

    public WindowsAiOcrService(SnippingSettings? settings = null)
    {
        _uiLanguage = settings?.Language ?? "zh-CN";
        _availability = GetAvailability();
        UnavailableReason = _availability.IsSupported
            ? null
            : UiText.OcrWindowsAiUnavailable(
                _uiLanguage,
                _availability.UnavailableReason ?? _availability.ReadyState.ToString());
    }

    public static Availability GetAvailability()
    {
        try
        {
            var state = TextRecognizer.GetReadyState();
            var supported = state is AIFeatureReadyState.Ready or AIFeatureReadyState.NotReady;
            return new Availability(
                supported,
                state,
                supported ? null : DescribeUnavailableState(state));
        }
        catch (Exception ex)
        {
            return new Availability(false, AIFeatureReadyState.NotSupportedOnCurrentSystem, ex.Message);
        }
    }

    public bool IsAvailable => _availability.IsSupported;

    public string? UnavailableReason { get; }

    public async Task<CoreOcrResult> RecognizeAsync(
        OcrImage image,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return new CoreOcrResult(Array.Empty<CoreOcrLine>(), UnavailableReason);

        try
        {
            ValidateImage(image);
            using var recognizer = await CreateRecognizerAsync(cancellationToken);
            var preparation = OcrImagePreprocessor.Prepare(
                image,
                WindowsOcrService.GetMaxImageDimension());
            var candidates = new List<CoreOcrLine>();

            foreach (var slice in preparation.Slices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var bitmap = CreateSoftwareBitmap(slice.Image);
                using var imageBuffer = ImageBuffer.CreateForSoftwareBitmap(bitmap);
                var recognizedText = await recognizer
                    .RecognizeTextFromImageAsync(imageBuffer)
                    .AsTask(cancellationToken);

                candidates.AddRange(recognizedText.Lines
                    .Select(CreateLine)
                    .Select(line => MapToOriginal(line, slice)));
            }

            var processingInfo = UiText.OcrImageProcessing(_uiLanguage, preparation);
            return new CoreOcrResult(
                WindowsOcrService.MergeRecognizedLines(candidates),
                null,
                CombineInfo(UiText.OcrWindowsAiUsing(_uiLanguage), processingInfo));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CoreOcrResult(
                Array.Empty<CoreOcrLine>(),
                UiText.OcrRecognitionFailed(_uiLanguage, ex.Message));
        }
    }

    private async Task<TextRecognizer> CreateRecognizerAsync(CancellationToken cancellationToken)
    {
        var state = TextRecognizer.GetReadyState();
        if (state == AIFeatureReadyState.NotReady)
        {
            var readyResult = await TextRecognizer.EnsureReadyAsync().AsTask(cancellationToken);
            if (readyResult.Status != AIFeatureReadyResultState.Success)
            {
                var detail = readyResult.ErrorDisplayText;
                if (string.IsNullOrWhiteSpace(detail))
                    detail = readyResult.ExtendedError?.Message;
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(detail) ? readyResult.Status.ToString() : detail);
            }

            state = TextRecognizer.GetReadyState();
        }

        if (state != AIFeatureReadyState.Ready)
        {
            throw new InvalidOperationException(
                UiText.OcrWindowsAiUnavailable(_uiLanguage, DescribeUnavailableState(state)));
        }

        return await TextRecognizer.CreateAsync().AsTask(cancellationToken);
    }

    private static CoreOcrLine CreateLine(RecognizedLine line)
    {
        var text = line.Text ?? string.Empty;
        var words = line.Words?
            .Select(static word => ToWordBox(word.BoundingBox))
            .ToArray() ?? Array.Empty<OcrWordBox>();

        if (words.Length > 0)
            return OcrLineGeometry.MergeWords(text, words);

        var bounds = ToBounds(line.BoundingBox);
        return new CoreOcrLine(text, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private static OcrWordBox ToWordBox(RecognizedTextBoundingBox bounds)
    {
        var rectangle = ToBounds(bounds);
        return new OcrWordBox(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
    }

    private static (int X, int Y, int Width, int Height) ToBounds(RecognizedTextBoundingBox bounds)
    {
        var points = new[] { bounds.TopLeft, bounds.TopRight, bounds.BottomRight, bounds.BottomLeft };
        var left = points.Min(static point => point.X);
        var top = points.Min(static point => point.Y);
        var right = points.Max(static point => point.X);
        var bottom = points.Max(static point => point.Y);
        var x = (int)Math.Floor(left);
        var y = (int)Math.Floor(top);
        return (x, y, Math.Max(1, (int)Math.Ceiling(right) - x), Math.Max(1, (int)Math.Ceiling(bottom) - y));
    }

    private static CoreOcrLine MapToOriginal(CoreOcrLine line, OcrImageSlice slice)
    {
        var scale = (float)slice.Scale;
        var x = (int)Math.Round((slice.OriginX + line.X) / scale);
        var y = (int)Math.Round((slice.OriginY + line.Y) / scale);
        var right = (int)Math.Round((slice.OriginX + line.X + line.Width) / scale);
        var bottom = (int)Math.Round((slice.OriginY + line.Y + line.Height) / scale);
        return line with
        {
            X = x,
            Y = y,
            Width = Math.Max(1, right - x),
            Height = Math.Max(1, bottom - y)
        };
    }

    private static SoftwareBitmap CreateSoftwareBitmap(OcrImage image)
    {
        var packed = new byte[checked(image.Width * image.Height * 4)];
        var source = image.Pixels.Span;
        var rowLength = checked(image.Width * 4);

        for (var row = 0; row < image.Height; row++)
        {
            source.Slice(row * image.Stride, rowLength)
                .CopyTo(packed.AsSpan(row * rowLength, rowLength));
        }

        var bitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            image.Width,
            image.Height,
            BitmapAlphaMode.Ignore);
        try
        {
            bitmap.CopyFromBuffer(CryptographicBuffer.CreateFromByteArray(packed));
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
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

    private static string DescribeUnavailableState(AIFeatureReadyState state) =>
        state switch
        {
            AIFeatureReadyState.NotSupportedOnCurrentSystem => "当前系统不支持",
            AIFeatureReadyState.NotCompatibleWithSystemHardware => "硬件不兼容（需要支持的 NPU 设备）",
            AIFeatureReadyState.CapabilityMissing => "应用缺少 Windows AI 模型能力",
            AIFeatureReadyState.DisabledByUser => "Windows AI 功能已被用户禁用",
            AIFeatureReadyState.OSUpdateNeeded => "需要更新 Windows",
            _ => state.ToString()
        };

    private static string? CombineInfo(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return second;
        if (string.IsNullOrWhiteSpace(second))
            return first;
        return $"{first} {second}";
    }
}
