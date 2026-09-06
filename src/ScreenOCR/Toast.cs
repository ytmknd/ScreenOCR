namespace ScreenOCR;

public sealed class Toast : Form
{
    private readonly System.Windows.Forms.Timer _lifeTimer = new();
    private readonly System.Windows.Forms.Timer _fadeTimer = new() { Interval = 20 };
    private readonly Action? _clickAction;

    private Toast(string title, string? detail, bool error, int durationMs, Rectangle? anchor, Action? clickAction)
    {
        _clickAction = clickAction;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = error ? Color.FromArgb(110, 25, 25) : Color.FromArgb(28, 28, 28);
        ForeColor = Color.White;
        ClientSize = new Size(390, string.IsNullOrWhiteSpace(detail) ? 64 : 94);
        Padding = new Padding(16, 12, 16, 10);

        var titleLabel = new Label { Text = title, Dock = DockStyle.Top, Height = 31, Font = new Font("Segoe UI", 11, FontStyle.Bold), ForeColor = ForeColor };
        var detailLabel = new Label { Text = detail ?? string.Empty, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9), ForeColor = Color.Gainsboro };
        Controls.Add(detailLabel);
        Controls.Add(titleLabel);
        foreach (Control control in Controls) control.Click += (_, _) => ClickNow();
        Click += (_, _) => ClickNow();

        Rectangle working = Screen.FromPoint(anchor.HasValue ? new Point(anchor.Value.Right, anchor.Value.Bottom) : System.Windows.Forms.Cursor.Position).WorkingArea;
        int x = anchor?.Right + 8 ?? working.Right - Width - 16;
        int y = anchor?.Bottom + 8 ?? working.Bottom - Height - 16;
        if (x + Width > working.Right || y + Height > working.Bottom) { x = working.Right - Width - 16; y = working.Bottom - Height - 16; }
        Location = new Point(Math.Max(working.Left, x), Math.Max(working.Top, y));

        _lifeTimer.Interval = durationMs;
        _lifeTimer.Tick += (_, _) => { _lifeTimer.Stop(); _fadeTimer.Start(); };
        _fadeTimer.Tick += (_, _) => { Opacity -= 0.1; if (Opacity <= 0.05) Close(); };
    }

    public static Toast ShowMessage(string title, string? detail = null, bool error = false, Rectangle? anchor = null, Action? clickAction = null)
    {
        var toast = new Toast(title, detail, error, error ? 4000 : 1200, anchor, clickAction);
        toast.Show();
        toast._lifeTimer.Start();
        return toast;
    }

    public static void ShowStandalone(string title, string? detail = null)
    {
        using var toast = new Toast(title, detail, true, 2200, null, null);
        toast.FormClosed += (_, _) => Application.ExitThread();
        toast.Show();
        toast._lifeTimer.Start();
        Application.Run();
    }

    private void ClickNow()
    {
        _clickAction?.Invoke();
        Close();
    }
}
