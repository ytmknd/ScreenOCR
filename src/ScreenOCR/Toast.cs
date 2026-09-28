using System.Diagnostics;
using System.Text;

namespace ScreenOCR;

/// <summary>折り返した本文。<see cref="Truncated"/> が真なら末尾を省略している。</summary>
public sealed record WrappedText(IReadOnlyList<string> Lines, bool Truncated);

public sealed class Toast : Form
{
    /// <summary>クリックする用のないトーストの表示時間。読めれば十分なので短い。</summary>
    private const int SuccessDurationMs = 1200;
    private const int ErrorDurationMs = 4000;

    /// <summary>
    /// クリックで実行できるトーストの表示時間。狙って押しにいくには 1.2 秒では短すぎるため、
    /// 操作できるものだけ長くする。
    /// </summary>
    private const int ActionableDurationMs = 8000;

    /// <summary>ホバーで延命し続けた場合でも必ず閉じるまでの上限。ポインタを置いたまま離席しても残さない。</summary>
    private const int MaxLifetimeMs = 30000;

    /// <summary>ホバー中に「まだポインタが乗っているか」を見直す間隔。</summary>
    private const int HoverRecheckMs = 250;

    /// <summary>操作できるトーストをカーソルから離す量。出た瞬間にホバー扱いにならない程度に近づける。</summary>
    private const int CursorOffset = 12;

    private const int CardWidth = 390;
    private const int TitleHeight = 31;

    /// <summary>本文の最大行数。ここを超えたぶんだけ末尾を省略し、全文はツールチップで見せる。</summary>
    private const int MaxDetailLines = 6;

    /// <summary>折り返し幅に持たせる余裕（px）。Label に再折り返しをさせないための保険。</summary>
    private const int WrapSafetyMargin = 10;

    private readonly System.Windows.Forms.Timer _lifeTimer = new();
    private readonly System.Windows.Forms.Timer _fadeTimer = new() { Interval = 20 };
    private readonly Stopwatch _lifetime = Stopwatch.StartNew();
    private readonly Action? _clickAction;
    private readonly Color _background;
    private readonly Color _hoverBackground;
    private readonly ToolTip? _toolTip;

    private Toast(string title, string? detail, bool error, int durationMs, Rectangle? anchor, Action? clickAction)
    {
        _clickAction = clickAction;
        bool actionable = clickAction is not null;
        _background = error ? Color.FromArgb(110, 25, 25) : Color.FromArgb(28, 28, 28);
        _hoverBackground = error ? Color.FromArgb(135, 34, 34) : Color.FromArgb(46, 46, 46);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = _background;
        ForeColor = Color.White;
        Padding = new Padding(16, 12, 16, 10);
        if (actionable) Cursor = Cursors.Hand;

        var titleLabel = new Label
        {
            Text = title, Dock = DockStyle.Top, Height = TitleHeight,
            Font = new Font("Segoe UI", 11, FontStyle.Bold), ForeColor = ForeColor,
            UseMnemonic = false
        };
        // 押せるトーストの本文はリンクとして描く。今までクリックできる手がかりが無かったため。
        var detailFont = new Font("Segoe UI", 9, actionable ? FontStyle.Underline : FontStyle.Regular);
        // URL には空白が無く単語折り返しが効かないので、こちらで文字単位に折り返す。
        // UseMnemonic を切らないと、クエリの "&" が下線付きのニーモニックとして食われて消える。
        int textWidth = CardWidth - Padding.Left - Padding.Right;
        // Label より少し狭く折り返す。こちらの計測のほうが 1 文字ぶん甘いと、Label がその行を
        // さらに割って最終行を枠外へ押し出す（実測では末尾の "entry.482912=4" が消えた）。
        WrappedText wrapped = WrapToWidth(detail ?? string.Empty, textWidth - WrapSafetyMargin, MaxDetailLines,
            text => TextRenderer.MeasureText(text, detailFont).Width);
        string detailText = string.Join(Environment.NewLine, wrapped.Lines);
        // 高さは折り返し後の文字列を実際に測って決める。Label がこちらの想定と違う折り返しを
        // しても収まるようにするため。
        int measured = string.IsNullOrWhiteSpace(detail) ? 0
            : TextRenderer.MeasureText(detailText, detailFont, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        int detailHeight = string.IsNullOrWhiteSpace(detail) ? 21 : Math.Max(41, measured + 6);
        ClientSize = new Size(CardWidth, Padding.Top + TitleHeight + detailHeight + Padding.Bottom);

        var detailLabel = new Label
        {
            Text = detailText,
            Dock = DockStyle.Fill,
            Font = detailFont,
            ForeColor = actionable ? Color.FromArgb(125, 190, 255) : Color.Gainsboro,
            UseMnemonic = false
        };
        // 省略したときだけでなく常に、全文をツールチップでも読めるようにする。
        if (actionable && !string.IsNullOrWhiteSpace(detail))
        {
            _toolTip = new ToolTip { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 30000, UseFading = false, UseAnimation = false };
            _toolTip.SetToolTip(detailLabel, detail);
            _toolTip.SetToolTip(titleLabel, detail);
        }
        Controls.Add(detailLabel);
        Controls.Add(titleLabel);
        foreach (Control control in Controls)
        {
            control.Click += (_, _) => ClickNow();
            control.MouseEnter += OnHoverChanged;
            control.MouseLeave += OnHoverChanged;
            if (actionable) control.Cursor = Cursors.Hand;
        }
        Click += (_, _) => ClickNow();
        MouseEnter += OnHoverChanged;
        MouseLeave += OnHoverChanged;

        Point cursor = System.Windows.Forms.Cursor.Position;
        if (actionable)
        {
            // 押せるトーストは指のすぐ横に出す。選択範囲の右下に固定すると、左上へ向かって
            // ドラッグしたときにカーソルから対角線の反対側へ出てしまう。
            Location = CalculateCursorLocation(cursor, Screen.FromPoint(cursor).WorkingArea, Size);
        }
        else
        {
            Rectangle working = Screen.FromPoint(anchor.HasValue ? new Point(anchor.Value.Right, anchor.Value.Bottom) : cursor).WorkingArea;
            int x = anchor?.Right + 8 ?? working.Right - Width - 16;
            int y = anchor?.Bottom + 8 ?? working.Bottom - Height - 16;
            if (x + Width > working.Right || y + Height > working.Bottom) { x = working.Right - Width - 16; y = working.Bottom - Height - 16; }
            Location = new Point(Math.Max(working.Left, x), Math.Max(working.Top, y));
        }

        _lifeTimer.Interval = durationMs;
        _lifeTimer.Tick += (_, _) =>
        {
            if (Gone) return;
            if (KeepAlive()) { _lifeTimer.Interval = HoverRecheckMs; return; }
            _lifeTimer.Stop();
            _fadeTimer.Start();
        };
        _fadeTimer.Tick += (_, _) =>
        {
            if (Gone) return;
            // 消えかけたところへポインタが来たら元に戻す。
            if (KeepAlive())
            {
                _fadeTimer.Stop();
                Opacity = 1;
                _lifeTimer.Interval = HoverRecheckMs;
                _lifeTimer.Start();
                return;
            }
            Opacity -= 0.1;
            if (Opacity <= 0.05) Close();
        };
    }

    /// <summary>
    /// 閉じた後もタイマーを動かしたままにしない。止めても投函済みの <c>WM_TIMER</c> が 1 回届くこと
    /// があるので、ハンドラー側でも <see cref="Gone"/> を見る（破棄済みフォームの <c>Bounds</c> や
    /// <c>Opacity</c> に触ると <see cref="ObjectDisposedException"/> になる）。
    /// </summary>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _lifeTimer.Stop();
        _fadeTimer.Stop();
        _lifeTimer.Dispose();
        _fadeTimer.Dispose();
        _toolTip?.Dispose();
        base.OnFormClosed(e);
    }

    private bool Gone => IsDisposed || Disposing || !IsHandleCreated;

    public static Toast ShowMessage(string title, string? detail = null, bool error = false, Rectangle? anchor = null, Action? clickAction = null)
    {
        var toast = new Toast(title, detail, error, CalculateDurationMs(error, clickAction is not null), anchor, clickAction);
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

    public static int CalculateDurationMs(bool error, bool actionable) =>
        actionable ? ActionableDurationMs : error ? ErrorDurationMs : SuccessDurationMs;

    /// <summary>
    /// 文字単位で折り返す。URL には空白が無いため、単語折り返しでは 1 行目しか出ない。
    /// <para>
    /// <b>省略するのは必ず末尾だけ</b>。URL は先頭にスキームとホストが来るので、末尾から削るかぎり
    /// 行き先を隠せない。先頭や中間を詰めると、そこに本当のホストを紛れ込ませられてしまう。
    /// </para>
    /// </summary>
    /// <param name="measure">与えた文字列の描画幅を返す。テストからは等幅として差し替える。</param>
    public static WrappedText WrapToWidth(string text, int maxWidth, int maxLines, Func<string, int> measure)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);

        var lines = new List<string>();
        string[] paragraphs = text.Replace("\r\n", "\n").Split('\n');
        for (int index = 0; index < paragraphs.Length; index++)
        {
            var current = new StringBuilder();
            foreach (char character in paragraphs[index])
            {
                // ここへ来る時点で、まだ置けていない文字が残っている＝省略が起きる。
                if (current.Length > 0 && measure(current.ToString() + character) > maxWidth)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                    if (lines.Count == maxLines) return new WrappedText(Elide(lines, maxWidth, measure), true);
                }
                current.Append(character);
            }
            lines.Add(current.ToString());
            if (lines.Count == maxLines && index < paragraphs.Length - 1)
                return new WrappedText(Elide(lines, maxWidth, measure), true);
        }
        return new WrappedText(lines, false);
    }

    /// <summary>最終行の末尾を削って省略記号を収める。</summary>
    private static IReadOnlyList<string> Elide(List<string> lines, int maxWidth, Func<string, int> measure)
    {
        string last = lines[^1];
        while (last.Length > 0 && measure(last + "…") > maxWidth) last = last[..^1];
        lines[^1] = last + "…";
        return lines;
    }

    /// <summary>
    /// 操作できるトーストの位置。カーソルの右下へわずかにずらして置き、作業領域から出る側だけ
    /// カーソルの反対側へ折り返す。
    /// </summary>
    public static Point CalculateCursorLocation(Point cursor, Rectangle workingArea, Size windowSize)
    {
        int x = cursor.X + CursorOffset;
        int y = cursor.Y + CursorOffset;
        if (x + windowSize.Width > workingArea.Right) x = cursor.X - CursorOffset - windowSize.Width;
        if (y + windowSize.Height > workingArea.Bottom) y = cursor.Y - CursorOffset - windowSize.Height;
        x = Math.Clamp(x, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - windowSize.Width));
        y = Math.Clamp(y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - windowSize.Height));
        return new Point(x, y);
    }

    /// <summary>
    /// 押そうとしている最中に消えないよう、ポインタが乗っている間は寿命を延ばす。
    /// 延ばすのは操作できるトーストだけで、置きっぱなしに備えて合計時間に上限を設ける。
    /// </summary>
    private bool KeepAlive() =>
        _clickAction is not null
        && !Gone
        && _lifetime.ElapsedMilliseconds < MaxLifetimeMs
        && Bounds.Contains(System.Windows.Forms.Cursor.Position);

    private void OnHoverChanged(object? sender, EventArgs e)
    {
        if (_clickAction is null || Gone) return;
        BackColor = Bounds.Contains(System.Windows.Forms.Cursor.Position) ? _hoverBackground : _background;
    }

    private void ClickNow()
    {
        _clickAction?.Invoke();
        Close();
    }
}
