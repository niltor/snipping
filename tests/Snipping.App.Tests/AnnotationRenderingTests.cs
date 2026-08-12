using System.Drawing.Imaging;

namespace Snipping.App.Tests;

public sealed class AnnotationRenderingTests
{
    [Fact]
    public void Rectangle_FillModeUsesConfiguredOpacity()
    {
        using var bitmap = NewTransparentBitmap(40, 40);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            new RectangleAnnotation
            {
                Start = new Point(4, 4),
                End = new Point(34, 34),
                Color = Color.Red,
                Opacity = 50,
                RenderMode = ShapeRenderMode.Fill
            }.Draw(graphics, bitmap);
        }

        var center = bitmap.GetPixel(20, 20);
        Assert.InRange(center.A, 120, 135);
        Assert.True(center.R > 200);
    }

    [Fact]
    public void Rectangle_OutlineModeLeavesInteriorUnpainted()
    {
        using var bitmap = NewTransparentBitmap(40, 40);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            new RectangleAnnotation
            {
                Start = new Point(4, 4),
                End = new Point(34, 34),
                Color = Color.Red,
                RenderMode = ShapeRenderMode.Outline
            }.Draw(graphics, bitmap);
        }

        Assert.Equal(0, bitmap.GetPixel(20, 20).A);
        Assert.True(bitmap.GetPixel(4, 20).A > 0);
    }

    [Fact]
    public void TextBoundsReflectBoldAndItalicStyle()
    {
        var regular = new TextAnnotation
        {
            Position = Point.Empty,
            Text = "Sample",
            FontSize = 28
        };
        var styled = new TextAnnotation
        {
            Position = Point.Empty,
            Text = "Sample",
            FontSize = 28,
            Bold = true,
            Italic = true
        };

        Assert.True(styled.GetBounds().Width >= regular.GetBounds().Width);
        Assert.True(styled.GetBounds().Height >= regular.GetBounds().Height);
    }

    [Fact]
    public void Arrow_DoubleModeAddsHeadAtStart()
    {
        using var single = NewTransparentBitmap(120, 60);
        using var dual = NewTransparentBitmap(120, 60);
        using (var graphics = Graphics.FromImage(single))
        {
            new ArrowAnnotation
            {
                Start = new Point(18, 30),
                End = new Point(102, 30),
                ArrowHead = ArrowHeadMode.Single
            }.Draw(graphics, single);
        }
        using (var graphics = Graphics.FromImage(dual))
        {
            new ArrowAnnotation
            {
                Start = new Point(18, 30),
                End = new Point(102, 30),
                ArrowHead = ArrowHeadMode.Double
            }.Draw(graphics, dual);
        }

        Assert.True(CountPainted(dual) > CountPainted(single));
    }

    [Fact]
    public void Arrow_DashedSingleModeLeavesVisibleGaps()
    {
        using var bitmap = NewTransparentBitmap(120, 60);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            new ArrowAnnotation
            {
                Start = new Point(8, 30),
                End = new Point(112, 30),
                ArrowHead = ArrowHeadMode.Single,
                StrokeStyle = LineStrokeStyle.Dashed
            }.Draw(graphics, bitmap);
        }

        var painted = 0;
        var gaps = 0;
        for (var x = 8; x <= 100; x++)
        {
            if (bitmap.GetPixel(x, 30).A > 0) painted++;
            else gaps++;
        }

        Assert.True(painted > 0);
        Assert.True(gaps > 0);
    }

    [Fact]
    public void Line_DashedModeLeavesVisibleGaps()
    {
        using var bitmap = NewTransparentBitmap(120, 60);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            new LineAnnotation
            {
                Start = new Point(8, 30),
                End = new Point(112, 30),
                StrokeStyle = LineStrokeStyle.Dashed
            }.Draw(graphics, bitmap);
        }

        var painted = 0;
        var gaps = 0;
        for (var x = 8; x <= 112; x++)
        {
            if (bitmap.GetPixel(x, 30).A > 0) painted++;
            else gaps++;
        }

        Assert.True(painted > 0);
        Assert.True(gaps > 0);
    }

    [Fact]
    public void Line_DottedModeLeavesVisibleGaps()
    {
        using var bitmap = NewTransparentBitmap(120, 60);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            new LineAnnotation
            {
                Start = new Point(8, 30),
                End = new Point(112, 30),
                StrokeStyle = LineStrokeStyle.Dotted
            }.Draw(graphics, bitmap);
        }

        var painted = 0;
        var gaps = 0;
        for (var x = 8; x <= 112; x++)
        {
            if (bitmap.GetPixel(x, 30).A > 0) painted++;
            else gaps++;
        }

        Assert.True(painted > 0);
        Assert.True(gaps > 0);
    }

    [Fact]
    public void MosaicBrushDoesNotModifySourceBitmap()
    {
        using var source = new Bitmap(80, 80, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(source))
        {
            using var brush = new SolidBrush(Color.CornflowerBlue);
            graphics.FillRectangle(brush, new Rectangle(0, 0, source.Width, source.Height));
        }

        var before = source.GetPixel(30, 30);
        using var output = new Bitmap(80, 80, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(output))
        {
            new MosaicBrushAnnotation
            {
                Points = [new Point(12, 30), new Point(68, 30)],
                BrushWidth = 50,
                BlockSize = 10
            }.Draw(graphics, source);
        }

        Assert.Equal(before, source.GetPixel(30, 30));
        Assert.True(output.GetPixel(30, 30).A > 0);
    }

    [Fact]
    public void ToolOptionsNormalizeToSupportedRanges()
    {
        var options = new AnnotationToolOptions
        {
            ShapeOpacity = 1,
            TextFontSize = 200,
            MosaicBrushWidth = 1
        };

        options.Normalize();

        Assert.Equal(10, options.ShapeOpacity);
        Assert.Equal(100, options.TextFontSize);
        Assert.Equal(5, options.MosaicBrushWidth);
    }

    [Fact]
    public void ToolOptionsAreIndependentAndCommittedStyleIsStable()
    {
        var rectangleOptions = new AnnotationToolOptions
        {
            ShapeMode = ShapeRenderMode.Fill,
            ShapeOpacity = 25
        };
        var arrowOptions = new AnnotationToolOptions();
        var committed = new RectangleAnnotation
        {
            Start = Point.Empty,
            End = new Point(20, 20),
            Opacity = rectangleOptions.ShapeOpacity,
            RenderMode = rectangleOptions.ShapeMode
        };

        rectangleOptions.ShapeMode = ShapeRenderMode.Outline;
        rectangleOptions.ShapeOpacity = 100;

        Assert.Equal(ArrowHeadMode.Single, arrowOptions.ArrowHead);
        Assert.Equal(25, committed.Opacity);
        Assert.Equal(ShapeRenderMode.Fill, committed.RenderMode);
    }

    [Fact]
    public void FeatureGateDefaultsToEnabledAndCanBeDisabled()
    {
        Assert.True(new FeatureEntitlements().AnnotationEnhancementsEnabled);
        Assert.False(new FeatureEntitlements { AnnotationEnhancementsEnabled = false }.AnnotationEnhancementsEnabled);
    }

    private static Bitmap NewTransparentBitmap(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        return bitmap;
    }

    private static int CountPainted(Bitmap bitmap)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, y).A > 0)
                count++;
        return count;
    }
}
