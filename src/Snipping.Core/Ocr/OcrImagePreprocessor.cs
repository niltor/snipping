namespace Snipping.Core.Ocr;

public sealed record OcrImageSlice(OcrImage Image, int OriginX, int OriginY, double Scale);

public sealed record OcrImagePreparation(
    IReadOnlyList<OcrImageSlice> Slices,
    double Scale,
    bool WasUpscaled,
    bool WasTiled);

/// <summary>
/// Prepares screenshot pixels for the Windows OCR backends.
/// Small selections are enlarged to make small glyphs easier to recognize;
/// oversized images are split into overlapping slices so no backend receives
/// an image beyond the Windows OCR dimension limit.
/// </summary>
public static class OcrImagePreprocessor
{
    private const int SmallImageLongSide = 1600;
    private const int SmallImageShortSide = 500;
    private const double SmallImageScale = 2.0;
    private const int TileOverlap = 256;

    public static OcrImagePreparation Prepare(OcrImage image, int maxDimension)
    {
        ValidateImage(image);
        if (maxDimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDimension));

        var scale = ShouldUpscale(image.Width, image.Height) ? SmallImageScale : 1.0;
        var scaledWidth = checked((int)Math.Ceiling(image.Width * scale));
        var scaledHeight = checked((int)Math.Ceiling(image.Height * scale));

        if (scaledWidth <= maxDimension && scaledHeight <= maxDimension)
        {
            var scaled = ResizeRegion(
                image,
                0,
                0,
                image.Width,
                image.Height,
                scaledWidth,
                scaledHeight);

            return new OcrImagePreparation(
                [new OcrImageSlice(scaled, 0, 0, scale)],
                scale,
                scale > 1,
                false);
        }

        var slices = new List<OcrImageSlice>();
        var overlap = Math.Min(TileOverlap, Math.Max(1, maxDimension / 4));
        var step = Math.Max(1, maxDimension - overlap);

        for (var y = 0; y < scaledHeight; y += step)
        {
            var tileHeight = Math.Min(maxDimension, scaledHeight - y);
            for (var x = 0; x < scaledWidth; x += step)
            {
                var tileWidth = Math.Min(maxDimension, scaledWidth - x);
                var sourceLeft = Math.Clamp((int)Math.Floor(x / scale), 0, image.Width - 1);
                var sourceTop = Math.Clamp((int)Math.Floor(y / scale), 0, image.Height - 1);
                var sourceRight = Math.Clamp((int)Math.Ceiling((x + tileWidth) / scale), sourceLeft + 1, image.Width);
                var sourceBottom = Math.Clamp((int)Math.Ceiling((y + tileHeight) / scale), sourceTop + 1, image.Height);

                var tile = ResizeRegion(
                    image,
                    sourceLeft,
                    sourceTop,
                    sourceRight - sourceLeft,
                    sourceBottom - sourceTop,
                    tileWidth,
                    tileHeight);
                slices.Add(new OcrImageSlice(tile, x, y, scale));

                if (x + tileWidth >= scaledWidth)
                    break;
            }

            if (y + tileHeight >= scaledHeight)
                break;
        }

        return new OcrImagePreparation(slices, scale, scale > 1, true);
    }

    private static bool ShouldUpscale(int width, int height) =>
        Math.Max(width, height) <= SmallImageLongSide
        || Math.Min(width, height) <= SmallImageShortSide;

    private static OcrImage ResizeRegion(
        OcrImage source,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        if (sourceWidth == targetWidth && sourceHeight == targetHeight)
            return CopyRegion(source, sourceX, sourceY, sourceWidth, sourceHeight);

        var target = new byte[checked(targetWidth * targetHeight * 4)];
        var sourcePixels = source.Pixels.Span;

        for (var targetY = 0; targetY < targetHeight; targetY++)
        {
            var sourceYPosition = sourceY + ((targetY + 0.5) * sourceHeight / targetHeight) - 0.5;
            var sourceY0 = Math.Clamp((int)Math.Floor(sourceYPosition), sourceY, sourceY + sourceHeight - 1);
            var sourceY1 = Math.Min(sourceY0 + 1, sourceY + sourceHeight - 1);
            var yFraction = Math.Clamp(sourceYPosition - Math.Floor(sourceYPosition), 0, 1);

            for (var targetX = 0; targetX < targetWidth; targetX++)
            {
                var sourceXPosition = sourceX + ((targetX + 0.5) * sourceWidth / targetWidth) - 0.5;
                var sourceX0 = Math.Clamp((int)Math.Floor(sourceXPosition), sourceX, sourceX + sourceWidth - 1);
                var sourceX1 = Math.Min(sourceX0 + 1, sourceX + sourceWidth - 1);
                var xFraction = Math.Clamp(sourceXPosition - Math.Floor(sourceXPosition), 0, 1);

                var topLeft = sourcePixels.Slice((sourceY0 * source.Stride) + (sourceX0 * 4), 4);
                var topRight = sourcePixels.Slice((sourceY0 * source.Stride) + (sourceX1 * 4), 4);
                var bottomLeft = sourcePixels.Slice((sourceY1 * source.Stride) + (sourceX0 * 4), 4);
                var bottomRight = sourcePixels.Slice((sourceY1 * source.Stride) + (sourceX1 * 4), 4);
                var targetOffset = (targetY * targetWidth + targetX) * 4;

                for (var channel = 0; channel < 4; channel++)
                {
                    var top = topLeft[channel] + (topRight[channel] - topLeft[channel]) * xFraction;
                    var bottom = bottomLeft[channel] + (bottomRight[channel] - bottomLeft[channel]) * xFraction;
                    target[targetOffset + channel] = (byte)Math.Clamp(
                        (int)Math.Round(top + (bottom - top) * yFraction),
                        0,
                        255);
                }
            }
        }

        return new OcrImage(target, targetWidth, targetHeight, checked(targetWidth * 4));
    }

    private static OcrImage CopyRegion(OcrImage source, int sourceX, int sourceY, int width, int height)
    {
        var target = new byte[checked(width * height * 4)];
        var sourcePixels = source.Pixels.Span;
        var rowLength = checked(width * 4);

        for (var row = 0; row < height; row++)
        {
            var sourceOffset = checked((sourceY + row) * source.Stride + sourceX * 4);
            sourcePixels.Slice(sourceOffset, rowLength)
                .CopyTo(target.AsSpan(row * rowLength, rowLength));
        }

        return new OcrImage(target, width, height, rowLength);
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
