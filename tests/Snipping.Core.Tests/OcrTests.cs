using Snipping.Core.Ocr;

namespace Snipping.Core.Tests;

public sealed class OcrTests
{
    [Fact]
    public void MergeWords_UsesUnionOfWordBounds()
    {
        var line = OcrLineGeometry.MergeWords(
            "Hello 世界",
            [
                new OcrWordBox(10.4f, 20.2f, 30.1f, 12.1f),
                new OcrWordBox(45.7f, 19.8f, 24.2f, 13.4f)
            ]);

        Assert.Equal("Hello 世界", line.Text);
        Assert.Equal(10, line.X);
        Assert.Equal(19, line.Y);
        Assert.Equal(60, line.Width);
        Assert.Equal(15, line.Height);
    }

    [Fact]
    public void OrderLines_SortsByReadingPositionAndDropsInvalidLines()
    {
        var ordered = OcrLineGeometry.OrderLines(
        [
            new OcrTextLine("second", 10, 50, 40, 12),
            new OcrTextLine("first right", 60, 10, 40, 12),
            new OcrTextLine("first left", 10, 10, 40, 12),
            new OcrTextLine("", 0, 0, 0, 0)
        ]);

        Assert.Equal(["first left", "first right", "second"], ordered.Select(x => x.Text));
    }

    [Fact]
    public void OcrResult_ReportsEmptySuccessAndFailureSeparately()
    {
        Assert.True(OcrResult.Empty.IsSuccess);
        Assert.Empty(OcrResult.Empty.Lines);

        var failure = new OcrResult(Array.Empty<OcrTextLine>(), "language pack missing");
        Assert.False(failure.IsSuccess);
        Assert.Equal("language pack missing", failure.ErrorMessage);

        var selected = new OcrResult(Array.Empty<OcrTextLine>(), null, "using zh-Hans + en-US");
        Assert.True(selected.IsSuccess);
        Assert.Equal("using zh-Hans + en-US", selected.InfoMessage);
    }
}
