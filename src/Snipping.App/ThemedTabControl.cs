using System.ComponentModel;

namespace Snipping.App;

/// <summary>
/// Lightweight themed tab host for the settings dialog.
/// It deliberately uses a panel instead of the native TabControl so the
/// system window color cannot leak into the header or page frame.
/// </summary>
internal sealed class ThemedTabControl : Panel
{
    private readonly List<Panel> _pages = [];
    private int _selectedIndex = -1;
    private Size _itemSize = new(104, 32);

    public ThemedTabControl()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable,
            true);

        TabStop = true;
        AccessibleRole = AccessibleRole.PageTabList;
        BackColor = Color.FromArgb(22, 22, 22);
        HeaderColor = Color.FromArgb(34, 34, 34);
        SelectedHeaderColor = Color.FromArgb(58, 58, 58);
        HeaderBorderColor = Color.FromArgb(86, 86, 86);
        HeaderTextColor = Color.White;
    }

    public event EventHandler? SelectedIndexChanged;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HeaderColor { get; set; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SelectedHeaderColor { get; set; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HeaderBorderColor { get; set; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HeaderTextColor { get; set; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Size ItemSize
    {
        get => _itemSize;
        set
        {
            if (value.Width <= 0 || value.Height <= 0 || _itemSize == value)
                return;

            _itemSize = value;
            PerformLayout();
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var next = _pages.Count == 0 ? -1 : Math.Clamp(value, 0, _pages.Count - 1);
            if (_selectedIndex == next)
                return;

            _selectedIndex = next;
            UpdatePageVisibility();
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void AddPage(Panel page)
    {
        ArgumentNullException.ThrowIfNull(page);

        _pages.Add(page);
        page.Parent = this;
        page.Visible = false;

        if (_selectedIndex < 0)
            _selectedIndex = 0;

        UpdatePageVisibility();
        PerformLayout();
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        UpdatePageVisibility();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        using var border = new Pen(HeaderBorderColor);
        var headerHeight = Math.Min(ItemSize.Height, ClientSize.Height);

        for (var index = 0; index < _pages.Count; index++)
        {
            var bounds = new Rectangle(index * ItemSize.Width, 0, ItemSize.Width, headerHeight);
            bounds.Inflate(-1, -1);

            using var background = new SolidBrush(index == SelectedIndex ? SelectedHeaderColor : HeaderColor);
            e.Graphics.FillRectangle(background, bounds);
            e.Graphics.DrawRectangle(border, bounds);
            TextRenderer.DrawText(
                e.Graphics,
                _pages[index].Text,
                Font,
                bounds,
                HeaderTextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left || e.Y < 0 || e.Y >= ItemSize.Height)
            return;

        var index = e.X / ItemSize.Width;
        if (index >= 0 && index < _pages.Count)
        {
            Focus();
            SelectedIndex = index;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = e.Y < ItemSize.Height && e.X >= 0 && e.X < _pages.Count * ItemSize.Width
            ? Cursors.Hand
            : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Cursor = Cursors.Default;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var next = e.KeyCode switch
        {
            Keys.Left or Keys.Up => SelectedIndex - 1,
            Keys.Right or Keys.Down => SelectedIndex + 1,
            Keys.Home => 0,
            Keys.End => _pages.Count - 1,
            _ => int.MinValue
        };

        if (next != int.MinValue && _pages.Count > 0)
        {
            SelectedIndex = next;
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void UpdatePageVisibility()
    {
        var headerHeight = Math.Min(ItemSize.Height, ClientSize.Height);
        var pageBounds = new Rectangle(
            0,
            headerHeight,
            ClientSize.Width,
            Math.Max(0, ClientSize.Height - headerHeight));

        for (var index = 0; index < _pages.Count; index++)
        {
            var page = _pages[index];
            page.Bounds = pageBounds;
            page.Visible = index == SelectedIndex;
        }
    }
}
