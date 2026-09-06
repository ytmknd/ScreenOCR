namespace ScreenOCR;

/// <summary>OCR 実行中だけ表示する、フォーカスを奪わない最前面インジケーター。</summary>
public sealed class ProcessingIndicator : Form
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    public ProcessingIndicator(Rectangle anchor, string engineName)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(28, 28, 28);
        ForeColor = Color.White;
        ClientSize = new Size(300, 82);
        Padding = new Padding(16, 11, 16, 10);
        Cursor = Cursors.WaitCursor;
        AccessibleName = "OCR 処理中";

        var title = new Label
        {
            Text = "OCR 処理中…",
            Dock = DockStyle.Top,
            Height = 31,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = ForeColor,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var detail = new Label
        {
            Text = engineName,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.Gainsboro,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var progress = new ProgressBar
        {
            Dock = DockStyle.Bottom,
            Height = 6,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 24
        };
        Controls.Add(detail);
        Controls.Add(title);
        Controls.Add(progress);

        Rectangle working = Screen.FromRectangle(anchor).WorkingArea;
        Location = CalculateLocation(anchor, working, Size);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    public static Point CalculateLocation(Rectangle anchor, Rectangle workingArea, Size windowSize)
    {
        int x = anchor.Left + (anchor.Width - windowSize.Width) / 2;
        int y = anchor.Top + (anchor.Height - windowSize.Height) / 2;
        x = Math.Clamp(x, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - windowSize.Width));
        y = Math.Clamp(y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - windowSize.Height));
        return new Point(x, y);
    }
}
