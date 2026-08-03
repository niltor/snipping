namespace Snipping.Core.Ocr;

public readonly record struct OcrWordBox(float X, float Y, float Width, float Height);

public static class OcrLineGeometry
{
    public static OcrTextLine MergeWords(string text, IEnumerable<OcrWordBox> words)
    {
        var boxes = words
            .Where(static box => box.Width > 0 && box.Height > 0)
            .ToArray();
        if (boxes.Length == 0)
            return new OcrTextLine(text, 0, 0, 0, 0);

        var left = boxes.Min(static box => (int)Math.Floor(box.X));
        var top = boxes.Min(static box => (int)Math.Floor(box.Y));
        var right = boxes.Max(static box => (int)Math.Ceiling(box.X + box.Width));
        var bottom = boxes.Max(static box => (int)Math.Ceiling(box.Y + box.Height));

        return new OcrTextLine(text, left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    public static IReadOnlyList<OcrTextLine> OrderLines(IEnumerable<OcrTextLine> lines) =>
        lines
            .Where(static line => line.IsValid)
            .OrderBy(static line => line.Y)
            .ThenBy(static line => line.X)
            .ToArray();
}
