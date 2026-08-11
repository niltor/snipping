namespace Snipping.App.Tests;

public sealed class SmartSelectionCandidateTests
{
    [Fact]
    public void WholeWindowFallbackDoesNotReplaceARealCandidate()
    {
        var current = new Rectangle(100, 100, 240, 120);
        var incoming = new SmartSelectionCandidate(
            new Rectangle(0, 0, 1600, 900),
            "Window",
            IsWindowFallback: true,
            NeedsRefinement: true,
            IsContainer: true,
            Source: SmartSelectionSource.WindowFallback,
            Confidence: 20);

        Assert.False(DesktopSnippingOverlayForm.ShouldApplySmartCandidate(
            incoming,
            current,
            currentIsFallback: false,
            currentSource: SmartSelectionSource.Visual,
            currentConfidence: 70,
            pointerInsideCurrent: true));
    }

    [Fact]
    public void SmallerRefinedCandidateCanReplaceBroadNativeHost()
    {
        var current = new Rectangle(100, 100, 1200, 700);
        var incoming = new SmartSelectionCandidate(
            new Rectangle(420, 260, 220, 80),
            "Button",
            Source: SmartSelectionSource.Automation,
            Confidence: 84);

        Assert.True(DesktopSnippingOverlayForm.ShouldApplySmartCandidate(
            incoming,
            current,
            currentIsFallback: false,
            currentSource: SmartSelectionSource.NativeHwnd,
            currentConfidence: 58,
            pointerInsideCurrent: true));
    }

    [Fact]
    public void BroadContainerDoesNotDisplaceAConfidentLeaf()
    {
        var current = new Rectangle(420, 260, 220, 80);
        var incoming = new SmartSelectionCandidate(
            new Rectangle(100, 100, 1200, 700),
            "Pane",
            IsContainer: true,
            Source: SmartSelectionSource.Automation,
            Confidence: 58);

        Assert.False(DesktopSnippingOverlayForm.ShouldApplySmartCandidate(
            incoming,
            current,
            currentIsFallback: false,
            currentSource: SmartSelectionSource.Automation,
            currentConfidence: 84,
            pointerInsideCurrent: true));
    }
}
