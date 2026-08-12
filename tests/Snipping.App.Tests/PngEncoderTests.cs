using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace Snipping.App.Tests;

public sealed class PngEncoderTests
{
    [Fact]
    public void EncodeProducesValidLosslessPngAndImprovesCompression()
    {
        using var bitmap = new Bitmap(256, 192, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(255, 32, 40, 48));
            using var brush = new SolidBrush(Color.FromArgb(255, 240, 120, 20));
            graphics.FillRectangle(brush, 24, 30, 160, 80);
            graphics.DrawLine(Pens.White, 0, 0, bitmap.Width - 1, bitmap.Height - 1);
        }

        var encoded = PngEncoder.Encode(bitmap);
        using var decodedStream = new MemoryStream(encoded);
        using var decoded = new Bitmap(decodedStream);
        using var referencePng = new MemoryStream();
        bitmap.Save(referencePng, ImageFormat.Png);

        Assert.Equal(137, encoded[0]);
        Assert.Equal(80, encoded[1]);
        Assert.Equal(bitmap.Width, decoded.Width);
        Assert.Equal(bitmap.Height, decoded.Height);
        Assert.Equal(bitmap.GetPixel(50, 50).ToArgb(), decoded.GetPixel(50, 50).ToArgb());
        Assert.Equal(bitmap.GetPixel(200, 150).ToArgb(), decoded.GetPixel(200, 150).ToArgb());
        Assert.True(
            encoded.Length < referencePng.Length,
            $"Expected optimized PNG ({encoded.Length} bytes) to be smaller than GDI+ PNG ({referencePng.Length} bytes).");
    }
}
