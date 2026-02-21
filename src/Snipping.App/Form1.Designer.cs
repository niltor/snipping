namespace Snipping.App;

partial class Form1
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    ///  Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        toolStrip1 = new ToolStrip();
        captureRegionButton = new ToolStripButton();
        captureFullScreenButton = new ToolStripButton();
        captureWindowButton = new ToolStripButton();
        toolStripSeparator1 = new ToolStripSeparator();
        annotationToolDropDown = new ToolStripComboBox();
        toolStripSeparator2 = new ToolStripSeparator();
        saveButton = new ToolStripButton();
        copyButton = new ToolStripButton();
        pictureBox = new PictureBox();
        statusStrip1 = new StatusStrip();
        statusLabel = new ToolStripStatusLabel();
        toolStrip1.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)pictureBox).BeginInit();
        statusStrip1.SuspendLayout();
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1000, 650);
        Controls.Add(pictureBox);
        Controls.Add(statusStrip1);
        Controls.Add(toolStrip1);
        MinimumSize = new Size(900, 500);
        Name = "Form1";
        Text = "Snipping";
        toolStrip1.Items.AddRange(new ToolStripItem[] { captureRegionButton, captureFullScreenButton, captureWindowButton, toolStripSeparator1, annotationToolDropDown, toolStripSeparator2, saveButton, copyButton });
        toolStrip1.Location = new Point(0, 0);
        toolStrip1.Name = "toolStrip1";
        toolStrip1.Size = new Size(1000, 25);
        captureRegionButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        captureRegionButton.Text = "区域截图";
        captureRegionButton.Click += CaptureRegionButton_Click;
        captureFullScreenButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        captureFullScreenButton.Text = "全屏截图";
        captureFullScreenButton.Click += CaptureFullScreenButton_Click;
        captureWindowButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        captureWindowButton.Text = "窗口截图";
        captureWindowButton.Click += CaptureWindowButton_Click;
        annotationToolDropDown.DropDownStyle = ComboBoxStyle.DropDownList;
        annotationToolDropDown.Name = "annotationToolDropDown";
        annotationToolDropDown.Size = new Size(130, 25);
        saveButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        saveButton.Text = "保存";
        saveButton.Click += SaveButton_Click;
        copyButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        copyButton.Text = "复制";
        copyButton.Click += CopyButton_Click;
        pictureBox.Dock = DockStyle.Fill;
        pictureBox.Location = new Point(0, 25);
        pictureBox.Name = "pictureBox";
        pictureBox.Size = new Size(1000, 603);
        pictureBox.SizeMode = PictureBoxSizeMode.StretchImage;
        pictureBox.TabStop = false;
        pictureBox.MouseDown += PictureBox_MouseDown;
        pictureBox.MouseMove += PictureBox_MouseMove;
        pictureBox.MouseUp += PictureBox_MouseUp;
        statusStrip1.Items.AddRange(new ToolStripItem[] { statusLabel });
        statusStrip1.Location = new Point(0, 628);
        statusStrip1.Name = "statusStrip1";
        statusStrip1.Size = new Size(1000, 22);
        statusLabel.Name = "statusLabel";
        statusLabel.Size = new Size(32, 17);
        statusLabel.Text = "就绪";
        toolStrip1.ResumeLayout(false);
        toolStrip1.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)pictureBox).EndInit();
        statusStrip1.ResumeLayout(false);
        statusStrip1.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private ToolStrip toolStrip1;
    private ToolStripButton captureRegionButton;
    private ToolStripButton captureFullScreenButton;
    private ToolStripButton captureWindowButton;
    private ToolStripSeparator toolStripSeparator1;
    private ToolStripComboBox annotationToolDropDown;
    private ToolStripSeparator toolStripSeparator2;
    private ToolStripButton saveButton;
    private ToolStripButton copyButton;
    private PictureBox pictureBox;
    private StatusStrip statusStrip1;
    private ToolStripStatusLabel statusLabel;
}
