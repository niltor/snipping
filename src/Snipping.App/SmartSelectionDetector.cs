using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Snipping.App;

internal sealed record SmartSelectionCandidate(Rectangle ScreenBounds, string Description);

/// <summary>
/// Finds common controls behind the full-screen capture overlay.
/// UI Automation gives us semantic bounds for buttons, edits, menus and panes;
/// the top-level window fallback keeps window selection useful for apps that do
/// not expose a detailed automation tree.
/// </summary>
internal sealed class SmartSelectionDetector
{
    private const uint GwHwndNext = 2;

    private static readonly HashSet<int> SupportedControlTypes =
    [
        ControlType.Window.Id,
        ControlType.Pane.Id,
        ControlType.Group.Id,
        ControlType.ToolBar.Id,
        ControlType.Menu.Id,
        ControlType.MenuItem.Id,
        ControlType.Button.Id,
        ControlType.CheckBox.Id,
        ControlType.RadioButton.Id,
        ControlType.ComboBox.Id,
        ControlType.Edit.Id,
        ControlType.List.Id,
        ControlType.ListItem.Id,
        ControlType.Tab.Id,
        ControlType.TabItem.Id,
        ControlType.Tree.Id,
        ControlType.TreeItem.Id,
        ControlType.Hyperlink.Id,
        ControlType.Document.Id,
        ControlType.DataGrid.Id,
        ControlType.DataItem.Id
    ];

    public SmartSelectionCandidate? Detect(Point screenPoint, IntPtr excludedWindow)
    {
        var targetWindow = FindWindowUnderPoint(screenPoint, excludedWindow);
        if (targetWindow == IntPtr.Zero)
            return null;

        try
        {
            var root = AutomationElement.FromHandle(targetWindow);
            var best = FindSmallestSupportedElement(root, screenPoint);
            if (best is not null)
                return best;
        }
        catch (ElementNotAvailableException)
        {
            // The target may disappear while the user moves across windows.
        }
        catch (COMException)
        {
            // Some elevated or custom-rendered windows reject UIA access.
        }

        if (TryGetWindowBounds(targetWindow, out var windowBounds))
            return new SmartSelectionCandidate(windowBounds, "Window");

        return null;
    }

    private static SmartSelectionCandidate? FindSmallestSupportedElement(
        AutomationElement root,
        Point screenPoint)
    {
        var elements = new List<AutomationElement> { root };
        try
        {
            elements.AddRange(root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>());
        }
        catch (ElementNotAvailableException)
        {
            // Use the root element if the tree changes during enumeration.
        }
        catch (COMException)
        {
            // Use the root window fallback below.
        }

        SmartSelectionCandidate? best = null;
        var bestArea = long.MaxValue;
        foreach (var element in elements)
        {
            try
            {
                var info = element.Current;
                if (info.IsOffscreen || !SupportedControlTypes.Contains(info.ControlType.Id))
                    continue;

                var bounds = ToScreenRectangle(info.BoundingRectangle);
                if (bounds.Width < 6 || bounds.Height < 6 || !bounds.Contains(screenPoint))
                    continue;

                var area = (long)bounds.Width * bounds.Height;
                if (area >= bestArea)
                    continue;

                var description = string.IsNullOrWhiteSpace(info.Name)
                    ? info.LocalizedControlType
                    : info.Name;
                best = new SmartSelectionCandidate(bounds, description);
                bestArea = area;
            }
            catch (ElementNotAvailableException)
            {
                // Ignore elements removed while the user moves the mouse.
            }
            catch (COMException)
            {
                // Ignore a single provider failure and keep other candidates.
            }
        }

        return best;
    }

    private static Rectangle ToScreenRectangle(System.Windows.Rect bounds)
    {
        if (double.IsNaN(bounds.X) || double.IsNaN(bounds.Y)
            || double.IsNaN(bounds.Width) || double.IsNaN(bounds.Height)
            || double.IsInfinity(bounds.X) || double.IsInfinity(bounds.Y)
            || double.IsInfinity(bounds.Width) || double.IsInfinity(bounds.Height))
        {
            return Rectangle.Empty;
        }

        var left = (int)Math.Round(bounds.Left);
        var top = (int)Math.Round(bounds.Top);
        var right = (int)Math.Round(bounds.Right);
        var bottom = (int)Math.Round(bounds.Bottom);
        return right > left && bottom > top
            ? Rectangle.FromLTRB(left, top, right, bottom)
            : Rectangle.Empty;
    }

    private static IntPtr FindWindowUnderPoint(Point screenPoint, IntPtr excludedWindow)
    {
        // The overlay is topmost and covers the whole virtual screen. Walk the
        // desktop Z-order until we find the first visible window below it that
        // actually contains the pointer.
        var window = GetTopWindow(IntPtr.Zero);
        while (window != IntPtr.Zero)
        {
            if (window != excludedWindow
                && IsWindowVisible(window)
                && TryGetWindowBounds(window, out var bounds)
                && bounds.Contains(screenPoint))
            {
                return window;
            }

            window = GetWindow(window, GwHwndNext);
        }

        return IntPtr.Zero;
    }

    private static bool TryGetWindowBounds(IntPtr window, out Rectangle bounds)
    {
        if (GetWindowRect(window, out var nativeBounds))
        {
            bounds = Rectangle.FromLTRB(
                nativeBounds.Left,
                nativeBounds.Top,
                nativeBounds.Right,
                nativeBounds.Bottom);
            return bounds.Width > 0 && bounds.Height > 0;
        }

        bounds = Rectangle.Empty;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
}
