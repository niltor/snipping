using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Snipping.App;

/// <summary>
/// Creates the tray and settings-window icon from the selected Store artwork.
/// Keeping one source prevents the app icon from drifting from the package icon.
/// </summary>
internal static class AppIcon
{
    private const int Size = 32;
    // The selected artwork is a square canvas with generous outer margins.
    // Crop to the artwork area before producing the small tray/window icon.
    private const double ArtworkCropRatio = 0.66;
    private const double ArtworkCenterX = 0.546;
    private const double ArtworkCenterY = 0.516;
    private const string ResourceName = "Snipping.App.StoreIcon.png";

    public static Icon Create()
    {
        var assembly = typeof(AppIcon).Assembly;
        using var resource = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded app icon not found: {ResourceName}");
        using var source = new Bitmap(resource);
        using var bitmap = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var crop = GetArtworkCrop(source);
            graphics.DrawImage(source, new Rectangle(0, 0, Size, Size), crop, GraphicsUnit.Pixel);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var sourceIcon = Icon.FromHandle(handle);
            return (Icon)sourceIcon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Rectangle GetArtworkCrop(Bitmap source)
    {
        var side = Math.Max(1, (int)Math.Round(Math.Min(source.Width, source.Height) * ArtworkCropRatio));
        var centerX = (int)Math.Round(source.Width * ArtworkCenterX);
        var centerY = (int)Math.Round(source.Height * ArtworkCenterY);
        var left = Math.Clamp(centerX - side / 2, 0, source.Width - side);
        var top = Math.Clamp(centerY - side / 2, 0, source.Height - side);
        return new Rectangle(
            left,
            top,
            side,
            side);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
