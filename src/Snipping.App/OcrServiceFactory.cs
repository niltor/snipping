using Snipping.Core.Ocr;
using Snipping.Core.Settings;

namespace Snipping.App;

internal static class OcrServiceFactory
{
    public sealed record OcrBackendOption(OcrBackend Value, string ChineseName, string EnglishName)
    {
        public string GetDisplay(bool english) => english ? EnglishName : ChineseName;
        public override string ToString() => ChineseName;
    }

    public static IOcrService Create(SnippingSettings settings)
    {
        if (settings.OcrBackend == OcrBackend.WindowsAi
            && WindowsAiOcrService.GetAvailability().IsReady)
        {
            return new WindowsAiOcrService(settings);
        }

        return new WindowsOcrService(settings);
    }

    public static IReadOnlyList<OcrBackendOption> GetAvailableBackends()
    {
        var options = new List<OcrBackendOption>
        {
            new(OcrBackend.Windows, "Windows 系统 OCR", "Windows system OCR")
        };

        // Do not expose a backend that cannot be used immediately. In
        // particular, NotReady may mean that the model is not installed yet;
        // the user should not be offered a selection that will silently fail.
        if (WindowsAiOcrService.GetAvailability().IsReady)
        {
            options.Add(new OcrBackendOption(
                OcrBackend.WindowsAi,
                "Windows AI OCR（需兼容设备）",
                "Windows AI OCR (compatible device required)"));
        }

        return options;
    }
}
