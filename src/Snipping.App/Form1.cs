using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Snipping.Core.Capture;
using Snipping.Core.Export;
using Snipping.Core.Settings;

namespace Snipping.App;

public partial class Form1 : Form
{
    private const int HotKeyId = 1001;
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private readonly SettingsManager _settingsManager = new();
    private readonly ExportManager _exportManager = new();
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snipping", "settings.json");
    private SnippingSettings _settings = new();
    private Bitmap? _canvas;
    private Point? _dragStart;
    private bool _isHotKeyRegistered;

    public Form1()
    {
        InitializeComponent();
        annotationToolDropDown.Items.AddRange(["Rectangle", "Ellipse", "Arrow", "Text", "Highlight", "Mosaic"]);
        annotationToolDropDown.SelectedIndex = 0;
        TopMost = true;
        ShowInTaskbar = false;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        LoadSettings();
        TryRegisterConfiguredHotKey();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_isHotKeyRegistered)
        {
            UnregisterHotKey(Handle, HotKeyId);
            _isHotKeyRegistered = false;
        }

        _canvas?.Dispose();
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotKey)
        {
            CaptureByMode(_settings.DefaultCaptureMode);
        }

        base.WndProc(ref m);
    }

    private void LoadSettings()
    {
        _settings = _settingsManager.Load(_settingsPath);
        ShowInTaskbar = _settings.ShowEditorInTaskbar;
    }

    private void CaptureByMode(CaptureMode mode)
    {
        switch (mode)
        {
            case CaptureMode.Region:
                CaptureRegionButton_Click(this, EventArgs.Empty);
                break;
            case CaptureMode.Window:
                CaptureWindowButton_Click(this, EventArgs.Empty);
                break;
            default:
                CaptureFullScreenButton_Click(this, EventArgs.Empty);
                break;
        }
    }

    private void CaptureRegionButton_Click(object? sender, EventArgs e)
    {
        using var selector = new RegionSelectionForm();
        if (selector.ShowDialog(this) == DialogResult.OK && selector.SelectedRegion.Width > 0 && selector.SelectedRegion.Height > 0)
        {
            SetCanvas(CaptureScreenArea(selector.SelectedRegion));
            statusLabel.Text = $"已捕获区域 {selector.SelectedRegion.Width}x{selector.SelectedRegion.Height}";
        }
    }

    private void CaptureFullScreenButton_Click(object? sender, EventArgs e)
    {
        SetCanvas(CaptureScreenArea(SystemInformation.VirtualScreen));
        statusLabel.Text = "已捕获全屏";
    }

    private void CaptureWindowButton_Click(object? sender, EventArgs e)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
        {
            statusLabel.Text = "窗口捕获失败";
            return;
        }

        var region = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        SetCanvas(CaptureScreenArea(region));
        statusLabel.Text = "已捕获活动窗口";
    }

    private async void SaveButton_Click(object? sender, EventArgs e)
    {
        if (_canvas is null)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            InitialDirectory = _settings.SaveDirectory,
            FileName = _exportManager.BuildFileName(_settings.FileNamePrefix, _settings.DefaultExportFormat, DateTimeOffset.Now),
            Filter = "PNG|*.png|JPEG|*.jpg"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var format = Path.GetExtension(dialog.FileName).Equals(".png", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Png : ExportFormat.Jpeg;
            var data = ToImageBytes(_canvas, format, _settings.JpegQuality);
            var directory = Path.GetDirectoryName(dialog.FileName);
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = string.IsNullOrWhiteSpace(_settings.SaveDirectory) ? Environment.CurrentDirectory : _settings.SaveDirectory;
            }

            Directory.CreateDirectory(directory);
            var targetPath = Path.Combine(directory, Path.GetFileName(dialog.FileName));
            await File.WriteAllBytesAsync(targetPath, data);
            statusLabel.Text = $"已保存: {targetPath}";
        }
        catch (Exception ex)
        {
            statusLabel.Text = "保存失败";
            MessageBox.Show(this, $"保存截图时发生错误：{ex.Message}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void CopyButton_Click(object? sender, EventArgs e)
    {
        if (_canvas is null)
        {
            return;
        }

        try
        {
            Clipboard.SetImage(_canvas);
            statusLabel.Text = "已复制到剪贴板";
        }
        catch (Exception ex)
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "Snipping");
            const ExportFormat fallbackFormat = ExportFormat.Png;
            var tempPath = await _exportManager.ExportAsync(tempDirectory, "clipboard_fallback", new ExportRequest(ToImageBytes(_canvas, fallbackFormat, 90), fallbackFormat), DateTimeOffset.Now);
            statusLabel.Text = "剪贴板写入失败，已导出临时文件";
            MessageBox.Show(this, $"无法写入剪贴板：{ex.Message}\n已保存到：{tempPath}", "复制失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PictureBox_MouseDown(object? sender, MouseEventArgs e)
    {
        if (_canvas is null || e.Button != MouseButtons.Left)
        {
            return;
        }

        _dragStart = ScaleToImagePoint(e.Location);
    }

    private void PictureBox_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragStart is not null)
        {
            statusLabel.Text = $"正在标注: {_dragStart.Value.X},{_dragStart.Value.Y} -> {ScaleToImagePoint(e.Location).X},{ScaleToImagePoint(e.Location).Y}";
        }
    }

    private void PictureBox_MouseUp(object? sender, MouseEventArgs e)
    {
        if (_canvas is null || _dragStart is null)
        {
            return;
        }

        var start = _dragStart.Value;
        var end = ScaleToImagePoint(e.Location);
        var rect = Normalize(start, end);
        if (rect.Width < 2 || rect.Height < 2)
        {
            _dragStart = null;
            return;
        }

        using var g = Graphics.FromImage(_canvas);
        using var pen = new Pen(Color.Red, 3);
        var tool = annotationToolDropDown.SelectedItem?.ToString();
        switch (tool)
        {
            case "Ellipse":
                g.DrawEllipse(pen, rect);
                break;
            case "Arrow":
                DrawArrow(g, pen, start, end);
                break;
            case "Text":
                g.DrawString("Text", SystemFonts.DefaultFont, Brushes.Red, rect.Location);
                break;
            case "Highlight":
                using (var brush = new SolidBrush(Color.FromArgb(100, Color.Yellow)))
                {
                    g.FillRectangle(brush, rect);
                }

                break;
            case "Mosaic":
                ApplyMosaic(_canvas, rect);
                break;
            default:
                g.DrawRectangle(pen, rect);
                break;
        }

        pictureBox.Invalidate();
        _dragStart = null;
        statusLabel.Text = $"已应用标注: {tool}";
    }

    private static Rectangle Normalize(Point a, Point b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var w = Math.Abs(a.X - b.X);
        var h = Math.Abs(a.Y - b.Y);
        return new Rectangle(x, y, w, h);
    }

    private static void DrawArrow(Graphics graphics, Pen pen, Point start, Point end)
    {
        graphics.DrawLine(pen, start, end);
        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        var p1 = new Point((int)(end.X - 15 * Math.Cos(angle - Math.PI / 6)), (int)(end.Y - 15 * Math.Sin(angle - Math.PI / 6)));
        var p2 = new Point((int)(end.X - 15 * Math.Cos(angle + Math.PI / 6)), (int)(end.Y - 15 * Math.Sin(angle + Math.PI / 6)));
        graphics.DrawLine(pen, end, p1);
        graphics.DrawLine(pen, end, p2);
    }

    private static void ApplyMosaic(Bitmap bitmap, Rectangle rect)
    {
        var safeRect = Rectangle.Intersect(rect, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (safeRect.Width < 2 || safeRect.Height < 2)
        {
            return;
        }

        var w = Math.Max(1, safeRect.Width / 8);
        var h = Math.Max(1, safeRect.Height / 8);
        using var mosaic = new Bitmap(w, h);
        using (var g = Graphics.FromImage(mosaic))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(bitmap, new Rectangle(0, 0, w, h), safeRect, GraphicsUnit.Pixel);
        }

        using var graphics = Graphics.FromImage(bitmap);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(mosaic, safeRect);
    }

    private static byte[] ToImageBytes(Bitmap bitmap, ExportFormat format, int quality)
    {
        using var ms = new MemoryStream();
        if (format == ExportFormat.Png)
        {
            bitmap.Save(ms, ImageFormat.Png);
        }
        else
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(static x => x.FormatID == ImageFormat.Jpeg.Guid);
            var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
            bitmap.Save(ms, codec, parameters);
        }

        return ms.ToArray();
    }

    private Bitmap CaptureScreenArea(Rectangle area)
    {
        var bitmap = new Bitmap(area.Width, area.Height);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private Point ScaleToImagePoint(Point point)
    {
        if (_canvas is null)
        {
            return Point.Empty;
        }

        var xLong = (long)point.X * _canvas.Width / Math.Max(1, pictureBox.ClientSize.Width);
        var yLong = (long)point.Y * _canvas.Height / Math.Max(1, pictureBox.ClientSize.Height);
        var x = (int)Math.Clamp(xLong, int.MinValue, int.MaxValue);
        var y = (int)Math.Clamp(yLong, int.MinValue, int.MaxValue);
        return new Point(Math.Clamp(x, 0, _canvas.Width - 1), Math.Clamp(y, 0, _canvas.Height - 1));
    }

    private void SetCanvas(Bitmap bitmap)
    {
        var oldCanvas = _canvas;
        pictureBox.Image = null;
        _canvas = bitmap;
        pictureBox.Image = _canvas;
        oldCanvas?.Dispose();
    }

    private void TryRegisterConfiguredHotKey()
    {
        if (!TryParseHotKey(_settings.Hotkey, out var modifiers, out var key))
        {
            statusLabel.Text = "快捷键配置无效，已回退默认 Ctrl+Shift+S";
            modifiers = ModControl | ModShift;
            key = (uint)Keys.S;
        }

        _isHotKeyRegistered = RegisterHotKey(Handle, HotKeyId, modifiers, key);
        if (!_isHotKeyRegistered)
        {
            statusLabel.Text = "快捷键注册失败，请修改设置中的快捷键";
        }
    }

    private static bool TryParseHotKey(string hotKey, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        if (string.IsNullOrWhiteSpace(hotKey))
        {
            return false;
        }

        var parts = hotKey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "ALT":
                    modifiers |= ModAlt;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= ModWin;
                    break;
                default:
                    return false;
            }
        }

        if (!Enum.TryParse<Keys>(parts[^1], true, out var parsed))
        {
            return false;
        }

        key = (uint)parsed;
        return key != 0 && modifiers != 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
