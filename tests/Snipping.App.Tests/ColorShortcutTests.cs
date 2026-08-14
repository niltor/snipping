using System.Windows.Forms;

namespace Snipping.App.Tests;

public sealed class ColorShortcutTests
{
    [Theory]
    [InlineData(Keys.D1, 0)]
    [InlineData(Keys.D2, 1)]
    [InlineData(Keys.D3, 2)]
    [InlineData(Keys.D4, 3)]
    [InlineData(Keys.D5, 4)]
    [InlineData(Keys.NumPad1, 0)]
    [InlineData(Keys.NumPad2, 1)]
    [InlineData(Keys.NumPad3, 2)]
    [InlineData(Keys.NumPad4, 3)]
    [InlineData(Keys.NumPad5, 4)]
    public void NumberKeysSelectColorPaletteIndex(Keys key, int expectedIndex)
    {
        Assert.Equal(expectedIndex, DesktopSnippingOverlayForm.GetColorShortcutIndex(key));
    }

    [Theory]
    [InlineData(Keys.D0)]
    [InlineData(Keys.D6)]
    [InlineData(Keys.NumPad0)]
    [InlineData(Keys.NumPad6)]
    [InlineData(Keys.A)]
    public void OtherKeysDoNotSelectAColor(Keys key)
    {
        Assert.Equal(-1, DesktopSnippingOverlayForm.GetColorShortcutIndex(key));
    }
}
