using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using Windows.System.UserProfile;
using Snipping.Core.Ocr;
using Snipping.Core.Settings;
using CoreOcrLine = Snipping.Core.Ocr.OcrTextLine;
using CoreOcrResult = Snipping.Core.Ocr.OcrResult;

namespace Snipping.App;

/// <summary>
/// Windows-provided OCR adapter. This API requires package identity at runtime;
/// the MSIX build is the supported distribution for this service.
/// </summary>
public sealed class WindowsOcrService : IOcrService
{
    public sealed record OcrLanguageOption(string Value, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record Recognizer(string LanguageTag, OcrEngine Engine);
    private sealed record RecognizerSelection(IReadOnlyList<Recognizer> Recognizers, string? Message);

    private readonly IReadOnlyList<Recognizer> _recognizers;
    private readonly string? _selectionMessage;
    private readonly string _uiLanguage;

    public WindowsOcrService(SnippingSettings? settings = null)
    {
        _uiLanguage = settings?.Language ?? "zh-CN";
        try
        {
            var selection = CreateRecognizers(settings?.OcrPreferredLanguage, _uiLanguage);
            _recognizers = selection.Recognizers;
            _selectionMessage = selection.Message;
            if (_recognizers.Count == 0)
                UnavailableReason = UiText.OcrLanguagePackMissing(_uiLanguage);
        }
        catch (Exception ex)
        {
            _recognizers = Array.Empty<Recognizer>();
            _selectionMessage = null;
            UnavailableReason = UiText.OcrInitializationFailed(_uiLanguage, ex.Message);
        }
    }

    public static IReadOnlyList<OcrLanguageOption> GetAvailableLanguages()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages
                .OrderBy(language => language.LanguageTag, StringComparer.OrdinalIgnoreCase)
                .Select(language => new OcrLanguageOption(
                    language.LanguageTag,
                    string.IsNullOrWhiteSpace(language.NativeName)
                        ? language.LanguageTag
                        : $"{language.NativeName} ({language.LanguageTag})"))
                .ToArray();
        }
        catch
        {
            return Array.Empty<OcrLanguageOption>();
        }
    }

    public bool IsAvailable => _recognizers.Count > 0;

    public string? UnavailableReason { get; }

    public async Task<CoreOcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
    {
        if (_recognizers.Count == 0)
            return new CoreOcrResult(Array.Empty<CoreOcrLine>(), UnavailableReason);

        try
        {
            ValidateImage(image);
            var preparation = OcrImagePreprocessor.Prepare(image, GetMaxImageDimension());
            var candidates = new List<CoreOcrLine>();
            var failures = new List<Exception>();
            var attempts = 0;

            // Windows OCR uses one engine per language. Run the installed
            // profile languages plus Chinese and English, then merge results
            // that refer to the same visual line. This handles mixed text
            // such as "文件 Report 2026" without a third-party OCR model.
            foreach (var slice in preparation.Slices)
            {
                using var bitmap = CreateSoftwareBitmap(slice.Image);
                foreach (var recognizer in _recognizers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    attempts++;
                    try
                    {
                        var nativeResult = await recognizer.Engine
                            .RecognizeAsync(bitmap)
                            .AsTask(cancellationToken);
                        candidates.AddRange(nativeResult.Lines
                            .Select(CreateLine)
                            .Select(line => MapToOriginal(line, slice)));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new InvalidOperationException(
                            UiText.OcrEngineFailure(_uiLanguage, recognizer.LanguageTag, ex.Message), ex));
                    }
                }
            }

            if (candidates.Count == 0 && failures.Count == attempts && attempts > 0)
            {
                var detail = string.Join("；", failures.Select(static failure => failure.Message));
                return new CoreOcrResult(Array.Empty<CoreOcrLine>(), UiText.OcrRecognitionFailed(_uiLanguage, detail));
            }

            return new CoreOcrResult(
                MergeRecognizedLines(candidates),
                null,
                CombineInfo(_selectionMessage, UiText.OcrImageProcessing(_uiLanguage, preparation)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CoreOcrResult(Array.Empty<CoreOcrLine>(), UiText.OcrRecognitionFailed(_uiLanguage, ex.Message));
        }
    }

    private static RecognizerSelection CreateRecognizers(string? preferredLanguage, string uiLanguage)
    {
        var available = OcrEngine.AvailableRecognizerLanguages;
        var selectedTags = new List<string>();
        string? message = null;

        if (!string.IsNullOrWhiteSpace(preferredLanguage))
        {
            var preferredTag = FindBestLanguageMatch(preferredLanguage, available);
            if (preferredTag is null)
            {
                message = UiText.OcrPreferredUnavailable(uiLanguage, preferredLanguage);
                SelectAutomatically(available, selectedTags, ref message, uiLanguage);
            }
            else
            {
                AddTag(preferredTag, selectedTags);
                if (!IsEnglishTag(preferredTag))
                {
                    var englishTag = FindLanguageByPrefix("en", available);
                    if (englishTag is not null)
                        AddTag(englishTag, selectedTags);
                    message = englishTag is null
                        ? UiText.OcrUsingConfiguredLanguageWithoutEnglish(uiLanguage, preferredTag)
                        : UiText.OcrUsingConfiguredMixedLanguages(uiLanguage, preferredTag, englishTag);
                }
                else
                {
                    message = UiText.OcrUsingConfiguredLanguage(uiLanguage, preferredTag);
                }
            }
        }
        else
        {
            SelectAutomatically(available, selectedTags, ref message, uiLanguage);
        }

        var recognizers = new List<Recognizer>();
        foreach (var tag in selectedTags)
        {
            try
            {
                var engine = OcrEngine.TryCreateFromLanguage(new Language(tag));
                if (engine is not null)
                    recognizers.Add(new Recognizer(tag, engine));
            }
            catch
            {
                // One malformed or unavailable language must not prevent the
                // remaining installed recognizers from being used.
            }
        }

        if (recognizers.Count == 0)
        {
            var fallback = OcrEngine.TryCreateFromUserProfileLanguages();
            if (fallback is not null)
                recognizers.Add(new Recognizer(fallback.RecognizerLanguage.LanguageTag, fallback));
        }

        return new RecognizerSelection(recognizers, message);
    }

    private static void SelectAutomatically(
        IReadOnlyList<Language> available,
        ICollection<string> selectedTags,
        ref string? message,
        string uiLanguage)
    {
        var englishTag = FindLanguageByPrefix("en", available);
        var preferredNonEnglishTag = GlobalizationPreferences.Languages
            .Select(tag => FindBestLanguageMatch(tag, available))
            .FirstOrDefault(tag => tag is not null && !IsEnglishTag(tag));
        var nonEnglishTag = preferredNonEnglishTag
            ?? available.Select(language => language.LanguageTag).FirstOrDefault(tag => !IsEnglishTag(tag));

        if (englishTag is not null && nonEnglishTag is not null)
        {
            AddTag(nonEnglishTag, selectedTags);
            AddTag(englishTag, selectedTags);
            message = UiText.OcrUsingMixedLanguages(uiLanguage, nonEnglishTag, englishTag);
        }
        else if (englishTag is not null)
        {
            AddTag(englishTag, selectedTags);
        }
        else if (nonEnglishTag is not null)
        {
            AddTag(nonEnglishTag, selectedTags);
            var availableCount = available.Count(language => !IsEnglishTag(language.LanguageTag));
            if (availableCount > 1)
                message = UiText.OcrMultipleEngines(uiLanguage, nonEnglishTag);
        }
    }

    private static string? FindBestLanguageMatch(
        string preferredTag,
        IReadOnlyList<Language> available)
    {
        if (string.IsNullOrWhiteSpace(preferredTag))
            return null;

        var normalized = preferredTag.Replace('_', '-');
        var exact = available.FirstOrDefault(language =>
            language.LanguageTag.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact.LanguageTag;

        var baseTag = normalized.Split('-', 2)[0];
        return available
            .Where(language => language.LanguageTag.Equals(baseTag, StringComparison.OrdinalIgnoreCase)
                || language.LanguageTag.StartsWith(baseTag + "-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(language => language.LanguageTag, StringComparer.OrdinalIgnoreCase)
            .Select(static language => language.LanguageTag)
            .FirstOrDefault();
    }

    private static string? FindLanguageByPrefix(
        string prefix,
        IReadOnlyList<Language> available)
    {
        return available
            .Where(language => language.LanguageTag.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || language.LanguageTag.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(language => language.LanguageTag.Contains("Hans", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(language => language.LanguageTag, StringComparer.OrdinalIgnoreCase)
            .Select(static language => language.LanguageTag)
            .FirstOrDefault();
    }

    private static bool IsEnglishTag(string tag) =>
        tag.Equals("en", StringComparison.OrdinalIgnoreCase)
        || tag.StartsWith("en-", StringComparison.OrdinalIgnoreCase);

    private static void AddTag(string tag, ICollection<string> selectedTags)
    {
        if (!selectedTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            selectedTags.Add(tag);
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

    internal static IReadOnlyList<CoreOcrLine> MergeRecognizedLines(IEnumerable<CoreOcrLine> candidates)
    {
        var groups = new List<List<CoreOcrLine>>();
        foreach (var line in OcrLineGeometry.OrderLines(candidates))
        {
            var group = groups.FirstOrDefault(existing =>
                existing.Any(previous => IsSameVisualLine(previous, line)));
            if (group is null)
                groups.Add([line]);
            else
                group.Add(line);
        }

        return OcrLineGeometry.OrderLines(groups.Select(MergeLineGroup));
    }

    private static CoreOcrLine MergeLineGroup(IReadOnlyList<CoreOcrLine> group)
    {
        var mixed = group
            .Where(static line => HasCjk(line.Text) && HasLatin(line.Text))
            .OrderByDescending(static line => TextQuality(line.Text))
            .FirstOrDefault();
        if (mixed is not null)
            return mixed;

        var hasChinese = group.Any(static line => HasCjk(line.Text));
        var hasLatin = group.Any(static line => HasLatin(line.Text));
        if (hasChinese && hasLatin)
        {
            var text = string.Join(" ", group
                .OrderBy(static line => line.X)
                .Select(static line => line.Text.Trim())
                .Where(static text => text.Length > 0));
            return MergeBounds(text, group);
        }

        return group.OrderByDescending(static line => TextQuality(line.Text)).First();
    }

    private static CoreOcrLine MergeBounds(string text, IEnumerable<CoreOcrLine> lines) =>
        OcrLineGeometry.MergeWords(
            text,
            lines.Select(static line => new OcrWordBox(line.X, line.Y, line.Width, line.Height)));

    private static bool IsSameVisualLine(CoreOcrLine first, CoreOcrLine second)
    {
        var firstCenterY = first.Y + first.Height / 2f;
        var secondCenterY = second.Y + second.Height / 2f;
        var maxHeight = Math.Max(first.Height, second.Height);
        if (Math.Abs(firstCenterY - secondCenterY) > maxHeight * 0.8f)
            return false;

        var horizontalGap = Math.Max(
            first.X - (second.X + second.Width),
            second.X - (first.X + first.Width));
        return horizontalGap <= Math.Max(first.Width, second.Width) * 0.5f;
    }

    private static int TextQuality(string text)
    {
        var meaningful = text.Count(char.IsLetterOrDigit);
        var scriptCoverage = (HasCjk(text) ? 1000 : 0) + (HasLatin(text) ? 1000 : 0);
        return scriptCoverage + meaningful;
    }

    private static bool HasCjk(string text) => text.Any(static character =>
        character is >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF');

    private static bool HasLatin(string text) => text.Any(static character =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');

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
            BitmapAlphaMode.Ignore);

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

    internal static int GetMaxImageDimension()
    {
        try
        {
            return Math.Max(1, (int)OcrEngine.MaxImageDimension);
        }
        catch
        {
            return 10000;
        }
    }

    private static string? CombineInfo(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return second;
        if (string.IsNullOrWhiteSpace(second))
            return first;
        return $"{first} {second}";
    }
}
