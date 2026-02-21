namespace Snipping.App;

public sealed class RegionSelectionForm : Form
{
    private const int MinRegionLength = 2;
    private Point? _start;
    private Rectangle _current;

    public Rectangle SelectedRegion { get; private set; }

    public RegionSelectionForm()
    {
        BackColor = Color.Black;
        Opacity = 0.25;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
        ShowInTaskbar = false;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
        };
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _start = PointToScreen(e.Location);
            _current = Rectangle.Empty;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_start is null)
        {
            return;
        }

        _current = Normalize(_start.Value, PointToScreen(e.Location));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_start is null)
        {
            return;
        }

        SelectedRegion = Normalize(_start.Value, PointToScreen(e.Location));
        DialogResult = SelectedRegion.Width >= MinRegionLength && SelectedRegion.Height >= MinRegionLength ? DialogResult.OK : DialogResult.Cancel;
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_current.IsEmpty)
        {
            return;
        }

        var local = new Rectangle(_current.X - Left, _current.Y - Top, _current.Width, _current.Height);
        using var pen = new Pen(Color.DeepSkyBlue, 2);
        e.Graphics.DrawRectangle(pen, local);
        using var brush = new SolidBrush(Color.FromArgb(80, Color.DeepSkyBlue));
        e.Graphics.FillRectangle(brush, local);
    }

    private static Rectangle Normalize(Point a, Point b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }
}
