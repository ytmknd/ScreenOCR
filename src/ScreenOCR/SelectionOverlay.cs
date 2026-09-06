namespace ScreenOCR;

public sealed class SelectionOverlay : Form
{
    private readonly FrozenSnapshot _snapshot;
    private readonly AppConfig _config;
    private Point _start;
    private Point _current;
    private bool _dragging;
    private bool _finishing;

    public SelectionOverlay(FrozenSnapshot snapshot, AppConfig config)
    {
        _snapshot = snapshot;
        _config = config;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        Bounds = snapshot.VirtualBounds;
        StartPosition = FormStartPosition.Manual;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public event EventHandler<SelectionCompletedEventArgs>? SelectionCompleted;

    protected override bool ShowWithoutActivation => false;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.DrawImageUnscaled(_snapshot.Bitmap, 0, 0);
        using var shade = new SolidBrush(Color.FromArgb((int)Math.Round(255 * _config.OverlayDimPercent / 100.0), Color.Black));
        e.Graphics.FillRectangle(shade, ClientRectangle);
        if (!_dragging) return;
        Rectangle selection = Normalize(_start, _current);
        if (selection.Width > 0 && selection.Height > 0)
        {
            e.Graphics.DrawImage(_snapshot.Bitmap, selection, selection, GraphicsUnit.Pixel);
            Color border;
            try { border = ColorTranslator.FromHtml(_config.SelectionBorderColor); }
            catch (Exception) { border = Color.FromArgb(0, 120, 212); }
            using var pen = new Pen(border, 1);
            e.Graphics.DrawRectangle(pen, selection.X, selection.Y, selection.Width - 1, selection.Height - 1);
        }
        DrawSize(e.Graphics, selection);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { CancelSelection(); return; }
        if (e.Button != MouseButtons.Left) return;
        _start = _current = e.Location;
        _dragging = true;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        _current = e.Location;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_dragging || e.Button != MouseButtons.Left) return;
        Capture = false;
        _current = e.Location;
        Rectangle local = Normalize(_start, _current);
        if (local.Width < _config.MinSelectionSize || local.Height < _config.MinSelectionSize)
        {
            Finish(null, true);
            return;
        }
        local.Offset(_snapshot.VirtualBounds.Location);
        Finish(local, false);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { CancelSelection(); return; }
        if (e.Control && e.KeyCode == Keys.A)
        {
            Finish(Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds, false);
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_finishing) CancelSelection();
    }

    public void CancelSelection() => Finish(null, false);

    private void Finish(Rectangle? rectangle, bool tooSmall)
    {
        if (_finishing) return;
        _finishing = true;
        Hide();
        SelectionCompleted?.Invoke(this, new SelectionCompletedEventArgs(rectangle, tooSmall));
        Close();
    }

    private void DrawSize(Graphics graphics, Rectangle selection)
    {
        string text = $"{selection.Width} × {selection.Height}";
        Point cursorScreen = PointToScreen(_current);
        float dpiScale = ScreenCapture.GetDpiPercent(new Rectangle(cursorScreen, new Size(1, 1))) / 100f;
        using var font = new Font("Segoe UI", 10f * dpiScale, FontStyle.Regular, GraphicsUnit.Point);
        SizeF measured = graphics.MeasureString(text, font);
        int x = Math.Min(ClientSize.Width - (int)measured.Width - 12, Math.Max(4, _current.X + 18));
        int y = Math.Min(ClientSize.Height - (int)measured.Height - 10, Math.Max(4, _current.Y + 18));
        if (x < _current.X && y < _current.Y) { x = Math.Max(4, _current.X - (int)measured.Width - 18); y = Math.Max(4, _current.Y - (int)measured.Height - 18); }
        var box = new RectangleF(x - 4, y - 2, measured.Width + 8, measured.Height + 4);
        using var background = new SolidBrush(Color.FromArgb(220, 32, 32, 32));
        graphics.FillRectangle(background, box);
        graphics.DrawString(text, font, Brushes.White, x, y);
    }

    private static Rectangle Normalize(Point a, Point b) => Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
}

public sealed class SelectionCompletedEventArgs(Rectangle? selection, bool tooSmall) : EventArgs
{
    public Rectangle? Selection { get; } = selection;
    public bool TooSmall { get; } = tooSmall;
}
