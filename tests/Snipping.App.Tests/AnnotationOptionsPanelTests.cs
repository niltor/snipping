namespace Snipping.App.Tests;

public sealed class AnnotationOptionsPanelTests
{
    [Fact]
    public void PanelUsesCompactIconButtonsWithoutToolName()
    {
        using var panel = new AnnotationOptionsPanel("zh-CN");
        panel.Bind(AnnotationTool.Rectangle, new AnnotationToolOptions());

        var content = Assert.IsType<FlowLayoutPanel>(panel.Controls[0]);
        var buttons = content.Controls.OfType<RoundedButton>().ToArray();

        Assert.True(panel.Width < 400);
        Assert.DoesNotContain(buttons, button => !string.IsNullOrWhiteSpace(button.Text));
        Assert.DoesNotContain(
            content.Controls.OfType<Label>(),
            label => label.Text.Contains("矩形", StringComparison.Ordinal));
    }
}
