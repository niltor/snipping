namespace Snipping.App.Tests;

public sealed class ToolbarLayoutTests
{
    [Fact]
    public void ToolbarUsesSpaceBelowSelectionWhenAvailable()
    {
        var layout = DesktopSnippingOverlayForm.CalculateToolbarLayout(
            new Rectangle(100, 200, 500, 200),
            new Size(360, 46),
            new Size(280, 42),
            optionsVisible: true,
            new Size(1200, 900));

        Assert.Equal(ToolbarPlacement.BelowSelection, layout.Placement);
        Assert.Equal(new Point(100, 410), layout.Location);
    }

    [Fact]
    public void ToolbarMovesAboveSelectionWhenBottomSpaceIsInsufficient()
    {
        var layout = DesktopSnippingOverlayForm.CalculateToolbarLayout(
            new Rectangle(100, 700, 500, 180),
            new Size(360, 46),
            new Size(280, 42),
            optionsVisible: true,
            new Size(1200, 900));

        Assert.Equal(ToolbarPlacement.AboveSelection, layout.Placement);
        Assert.Equal(new Point(100, 598), layout.Location);
    }

    [Fact]
    public void ToolbarFallsInsideSelectionWhenNeitherOutsideSideCanFit()
    {
        var selection = new Rectangle(100, 100, 500, 100);
        var layout = DesktopSnippingOverlayForm.CalculateToolbarLayout(
            selection,
            new Size(360, 46),
            new Size(280, 42),
            optionsVisible: true,
            new Size(1200, 300));

        Assert.Equal(ToolbarPlacement.InsideSelection, layout.Placement);
        Assert.Equal(106, layout.Location.Y);
        Assert.InRange(layout.Location.Y, selection.Top, selection.Bottom);
    }

    [Fact]
    public void ToolbarRemainsWithinScreenWhenSelectionTouchesTopAndBottomBoundaries()
    {
        var layout = DesktopSnippingOverlayForm.CalculateToolbarLayout(
            new Rectangle(2, 100, 500, 100),
            new Size(360, 46),
            Size.Empty,
            optionsVisible: false,
            new Size(800, 300));

        var toolbarBounds = new Rectangle(layout.Location, new Size(360, 46));
        Assert.True(toolbarBounds.Left >= 10);
        Assert.True(toolbarBounds.Right <= 790);
        Assert.True(toolbarBounds.Top >= 10);
        Assert.True(toolbarBounds.Bottom <= 290);
    }

    [Fact]
    public void ToolbarUsesNonZeroWorkingAreaOriginForMultiMonitorLayouts()
    {
        var layout = DesktopSnippingOverlayForm.CalculateToolbarLayout(
            new Rectangle(-1800, 850, 600, 180),
            new Size(360, 46),
            Size.Empty,
            optionsVisible: false,
            new Rectangle(-1920, 0, 1920, 1080));

        Assert.Equal(ToolbarPlacement.AboveSelection, layout.Placement);
        Assert.InRange(layout.Location.X, -1910, -10);
        Assert.InRange(layout.Location.Y, 10, 1024);
    }
}
