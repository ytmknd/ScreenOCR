using System.Diagnostics;
using System.Reflection;

namespace ScreenOCR;

public sealed class TrayApp : ApplicationContext
{
    private AppConfig _config;
    private readonly Logger _logger;
    private readonly Icon _appIcon = LoadAppIcon();
    private readonly OcrService _ocr = new();
    private readonly HotKeyRegistration _hotKey = new();
    private readonly HotKeyWindow _window;
    private readonly NotifyIcon _notify;
    private SelectionOverlay? _overlay;
    private FrozenSnapshot? _snapshot;
    private bool _busy;

    public TrayApp()
    {
        var warnings = new List<string>();
        ConfigLoadResult loaded = AppConfigStore.Load(warnings.Add);
        _config = loaded.Config;
        _logger = new Logger(_config.LogRetentionDays);
        foreach (string warning in warnings) _logger.Warning(warning);
        _window = new HotKeyWindow(OnHotKey);
        _notify = new NotifyIcon
        {
            Text = "ScreenOCR", Icon = _appIcon, Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _notify.DoubleClick += (_, _) => BeginCapture();
        RegisterHotKey();
        IReadOnlyList<OcrLanguageInfo> languages = OcrService.GetAvailableLanguages();
        PpOcrV6SmallAvailability pp = PpOcrV6SmallService.GetAvailability(_config);
        _logger.Info($"起動。OCRエンジン={_config.OcrEngine}; PP-OCRv6 Small={(pp.Available ? $"{pp.Command} / rapidocr {pp.Version}" : "未検出")}; 利用可能言語: {string.Join(", ", languages.Select(x => x.Tag))}; MaxImageDimension={OcrService.MaxImageDimension}");
        if (loaded.HadError) Toast.ShowMessage("設定ファイルを読めませんでした", "既定値で起動しました。config.json を確認してください", true);
    }

    private void OnHotKey()
    {
        if (_overlay is not null) { _overlay.CancelSelection(); return; }
        if (_busy) return;
        BeginCapture();
    }

    private async void BeginCapture()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _snapshot = await Task.Run(ScreenCapture.CaptureVirtualDesktop);
            _overlay = new SelectionOverlay(_snapshot, _config);
            _overlay.SelectionCompleted += SelectionCompleted;
            _overlay.Show();
            _overlay.Activate();
        }
        catch (Exception ex)
        {
            _logger.Error("キャプチャ失敗", ex);
            Toast.ShowMessage("画面をキャプチャできませんでした", error: true);
            ClearSelection();
        }
    }

    private async void SelectionCompleted(object? sender, SelectionCompletedEventArgs e)
    {
        if (_overlay is not null) _overlay.SelectionCompleted -= SelectionCompleted;
        _overlay = null;
        if (e.Selection is null)
        {
            if (e.TooSmall) Toast.ShowMessage("範囲が小さすぎます", error: true);
            ClearSelection();
            return;
        }

        Rectangle selection = e.Selection.Value;
        Bitmap? crop = null;
        ProcessingIndicator? indicator = null;
        try
        {
            crop = _snapshot!.Crop(selection);
            _snapshot.Dispose();
            _snapshot = null;
            if (await TryHandleQrCodeAsync(crop, selection)) return;
            float dpiPercent = ScreenCapture.GetDpiPercent(selection);
            string engineName = _config.OcrEngine == OcrBackendNames.PpOcrV6Small ? "PP-OCRv6 Small" : "Windows OCR";
            indicator = new ProcessingIndicator(selection, engineName);
            indicator.Show();
            indicator.Update();
            OcrPipelineResult result = await Task.Run(async () => await _ocr.RecognizeAsync(crop, _config, dpiPercent));
            indicator.Close();
            indicator.Dispose();
            indicator = null;
            string attemptLog = string.Join(", ", result.Attempts.Select(x =>
                $"{x.Name}:valid={x.ValidChars}/implausible={x.ImplausibleChars}/score={x.Score:0.##}/{x.Scale:0.##}x"));
            _logger.Info($"OCR rect={selection.X},{selection.Y},{selection.Width},{selection.Height}; dpi={dpiPercent:0.#}%; attempts=[{attemptLog}]; selected={result.SelectedAttempt}; scale={result.Scale:0.##}x; score={result.Score:0.##}; elapsed={result.ElapsedMilliseconds}ms; final={result.Text.Length}chars");
            if (result.Failure == OcrFailure.NoRecognizerAvailable)
            {
                if (_config.OcrEngine == OcrBackendNames.PpOcrV6Small)
                    Toast.ShowMessage("PP-OCRv6 Small が見つかりません", result.ErrorMessage, true, selection, OpenPpOcrV6Docs);
                else
                    Toast.ShowMessage("OCR 言語がインストールされていません", "設定 > 時刻と言語 > 言語と地域 > 日本語 > 言語オプション > 光学式文字認識 を追加してください", true, selection, OpenLanguageSettings);
            }
            else if (result.Failure == OcrFailure.Timeout)
            {
                Toast.ShowMessage("OCR 処理がタイムアウトしました", result.ErrorMessage, true, selection);
            }
            else if (result.Failure == OcrFailure.ExternalEngineError)
            {
                _logger.Warning(result.ErrorMessage ?? "外部 OCR エンジンの実行に失敗しました");
                Toast.ShowMessage("PP-OCRv6 Small の実行に失敗しました", result.ErrorMessage, true, selection);
            }
            else if (result.Failure == OcrFailure.NoTextFound || string.IsNullOrWhiteSpace(result.Text))
            {
                Toast.ShowMessage("文字を認識できませんでした", "範囲を広げるか、拡大表示してから再試行してください", true, selection);
            }
            else if (!ClipboardWriter.TryWrite(_window.Handle, result.Text, _config.CopyImageToo ? crop : null))
            {
                Toast.ShowMessage("クリップボードを開けませんでした", "他のアプリが使用中の可能性があります", true, selection);
            }
            else
            {
                Toast.ShowMessage($"{result.Text.Length} 文字をコピーしました", $"{result.LanguageTag} / {result.Scale:0.0}x", false, selection);
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.Error("OCR がタイムアウトしました", ex);
            Toast.ShowMessage("OCR 処理がタイムアウトしました", "範囲を小さくして再試行してください", true, selection);
        }
        catch (Exception ex)
        {
            _logger.Error("OCR 処理に失敗しました", ex);
            Toast.ShowMessage("OCR 処理に失敗しました", ex.Message, true, selection);
        }
        finally
        {
            indicator?.Close();
            indicator?.Dispose();
            crop?.Dispose();
            ClearSelection();
        }
    }

    /// <summary>
    /// 選択範囲に QR コードがあればその内容をコピーして <c>true</c> を返す。無ければ <c>false</c> を返し、
    /// 呼び出し側は従来どおり OCR へ進む。走査は数十 ms で終わるため、処理中インジケーターは出さない。
    /// デコード結果は通知の本文にもログにも残さない（内容が URL や個人情報のことがあるため）。
    /// </summary>
    private async Task<bool> TryHandleQrCodeAsync(Bitmap crop, Rectangle selection)
    {
        if (!_config.QrCodeEnabled) return false;

        QrScanResult qr;
        try { qr = await Task.Run(() => QrCodeReader.Scan(crop)); }
        catch (Exception ex)
        {
            _logger.Warning($"QR 走査に失敗しました（OCR を続行します）: {ex.Message}");
            return false;
        }
        if (!qr.Found) return false;

        _logger.Info($"QR rect={selection.X},{selection.Y},{selection.Width},{selection.Height}; attempt={qr.Attempt}; codes={qr.Matches.Count}; elapsed={qr.ElapsedMilliseconds}ms; chars={qr.Text.Length}");
        if (!ClipboardWriter.TryWrite(_window.Handle, qr.Text, _config.CopyImageToo ? crop : null))
        {
            Toast.ShowMessage("クリップボードを開けませんでした", "他のアプリが使用中の可能性があります", true, selection);
            return true;
        }

        // URL は自動で開かない。開くのは利用者が通知をクリックしたときだけ。複数の QR があっても
        // 開くのは読む順で最初のリンク 1 つだけで、どれを開くかはホスト名の表示で分かる。
        Uri? url = QrCodeReader.FindFirstHttpUrl(qr.Matches);
        string copied = qr.Matches.Count == 1
            ? $"{qr.Text.Length} 文字をコピーしました"
            : $"{qr.Matches.Count} 件 / {qr.Text.Length} 文字をコピーしました";
        if (url is null)
        {
            Toast.ShowMessage("QR コードを読み取りました", copied, false, selection);
            return true;
        }

        // 行き先は省略せず全部出す。ホストだけでは、同じホストの下に置かれた転送や偽ログイン
        // （/login?next=... など）を見分けられないため。表示のみ punycode・制御文字除去済みで、
        // 実際に開くのは AbsoluteUri（QrCodeReader.DescribeUrl 参照）。
        string open = $"クリックで開く: {QrCodeReader.DescribeUrl(url)}";
        Toast.ShowMessage("QR コードを読み取りました",
            qr.Matches.Count == 1 ? open : $"{qr.Matches.Count} 件をコピー ・ {open}",
            false, selection, () => OpenPath(url.AbsoluteUri));
        return true;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("ScreenOCR") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("文字を読み取る (Ctrl+Alt+6)", null, (_, _) => BeginCapture());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildEngineMenu());
        menu.Items.Add(BuildLanguageMenu());
        menu.Items.Add(BuildMergeToggle());
        menu.Items.Add(Toggle("QR コードを先に読み取る", () => _config.QrCodeEnabled, x => _config.QrCodeEnabled = x));
        var processing = new ToolStripMenuItem("後処理");
        processing.DropDownItems.Add(Toggle("日本語の余分な空白を削除", () => _config.RemoveCjkSpaces, x => _config.RemoveCjkSpaces = x));
        processing.DropDownItems.Add(Toggle("行を連結する", () => _config.JoinLines, x => _config.JoinLines = x));
        processing.DropDownItems.Add(Toggle("暗背景を自動反転", () => _config.AutoInvert, x => _config.AutoInvert = x));
        processing.DropDownItems.Add(Toggle("縦書きを試行する", () => _config.TryVertical, x => _config.TryVertical = x));
        menu.Items.Add(processing);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Toggle("スタートアップに登録", () => Startup.IsEnabled(), value => { Startup.SetEnabled(value); _config.RunAtStartup = value; }));
        menu.Items.Add("設定ファイルを開く", null, (_, _) => OpenPath(AppConfigStore.FilePath));
        menu.Items.Add("設定を再読み込み", null, (_, _) => ReloadConfig());
        menu.Items.Add("ログフォルダーを開く", null, (_, _) => OpenPath(_logger.DirectoryPath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("ScreenOCR v1.0.0") { Enabled = false });
        menu.Items.Add("終了", null, (_, _) => ExitThread());
        return menu;
    }

    private ToolStripMenuItem BuildEngineMenu()
    {
        var parent = new ToolStripMenuItem("OCR エンジン");
        var windows = new ToolStripMenuItem("Windows OCR")
        {
            Checked = _config.OcrEngine == OcrBackendNames.Windows
        };
        windows.Click += (_, _) => SelectEngine(OcrBackendNames.Windows);
        parent.DropDownItems.Add(windows);

        PpOcrV6SmallAvailability ppAvailability = PpOcrV6SmallService.GetAvailability(_config);
        var pp = new ToolStripMenuItem(ppAvailability.Available ? "PP-OCRv6 Small" : "PP-OCRv6 Small（未検出）")
        {
            Checked = _config.OcrEngine == OcrBackendNames.PpOcrV6Small
        };
        pp.Click += (_, _) => SelectEngine(OcrBackendNames.PpOcrV6Small);
        parent.DropDownItems.Add(pp);
        parent.DropDownItems.Add(new ToolStripSeparator());
        parent.DropDownItems.Add("PP-OCRv6 Small の導入方法…", null, (_, _) => OpenPpOcrV6Docs());
        return parent;
    }

    private void SelectEngine(string engine)
    {
        _config.OcrEngine = engine;
        _ocr.ResetEngine();
        SaveConfig();
        _notify.ContextMenuStrip = BuildMenu();
    }

    /// <summary>
    /// 「自動」（単一エンジン・従来動作）と、実際に利用可能な言語のチェックボックス群。
    /// 言語を複数チェックすると <see cref="AppConfig.OcrLanguages"/> に順序付きで積まれ、
    /// 「両エンジンを統合する」が有効かつ 2 つ以上利用可能なら統合が働く。
    /// </summary>
    private ToolStripMenuItem BuildLanguageMenu()
    {
        var parent = new ToolStripMenuItem("OCR 言語") { Enabled = _config.OcrEngine == OcrBackendNames.Windows };
        IReadOnlyList<OcrLanguageInfo> languages = OcrService.GetAvailableLanguages();
        bool isAuto = _config.OcrLanguages is not { Count: > 0 };
        IReadOnlyList<string> effective = _config.EffectiveOcrLanguages;

        var autoItem = new ToolStripMenuItem("自動") { Checked = isAuto };
        autoItem.Click += (_, _) =>
        {
            _config.OcrLanguages = null;
            _config.OcrLanguage = "auto";
            _ocr.ResetEngine();
            SaveConfig();
            _notify.ContextMenuStrip = BuildMenu();
        };
        parent.DropDownItems.Add(autoItem);
        parent.DropDownItems.Add(new ToolStripSeparator());

        foreach (OcrLanguageInfo language in languages)
        {
            bool @checked = !isAuto && effective.Any(x => x.Equals(language.Tag, StringComparison.OrdinalIgnoreCase));
            var item = new ToolStripMenuItem($"{language.DisplayName} ({language.Tag})") { Checked = @checked };
            item.Click += (_, _) => ToggleLanguage(language.Tag);
            parent.DropDownItems.Add(item);
        }

        if (languages.Count == 0)
        {
            parent.DropDownItems.Add(new ToolStripSeparator());
            parent.DropDownItems.Add("OCR 言語機能をインストール…", null, (_, _) => OpenLanguageSettings());
        }
        return parent;
    }

    private void ToggleLanguage(string tag)
    {
        bool isAuto = _config.OcrLanguages is not { Count: > 0 };
        List<string> current = isAuto ? [] : _config.OcrLanguages!.ToList();
        if (current.Any(x => x.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            current.RemoveAll(x => x.Equals(tag, StringComparison.OrdinalIgnoreCase));
        else
            current.Add(tag);

        if (current.Count == 0) { _config.OcrLanguages = null; _config.OcrLanguage = "auto"; }
        else { _config.OcrLanguages = current; _config.OcrLanguage = current[0]; }
        _ocr.ResetEngine();
        SaveConfig();
        _notify.ContextMenuStrip = BuildMenu();
    }

    /// <summary>「両エンジンを統合する」トグル。利用可能な言語が 1 つだけなら無効化して理由を表示する。</summary>
    private ToolStripMenuItem BuildMergeToggle()
    {
        int availableCount = OcrService.GetAvailableLanguages().Count;
        bool canMerge = _config.OcrEngine == OcrBackendNames.Windows && availableCount >= 2;
        string label = _config.OcrEngine != OcrBackendNames.Windows
            ? "両エンジンを統合する（Windows OCR 専用）"
            : canMerge ? "両エンジンを統合する" : "両エンジンを統合する（利用可能な OCR 言語が 1 つのため無効）";
        var item = new ToolStripMenuItem(label)
        {
            Checked = _config.MergeEngines,
            CheckOnClick = canMerge,
            Enabled = canMerge
        };
        if (canMerge) item.CheckedChanged += (_, _) => { _config.MergeEngines = item.Checked; SaveConfig(); };
        return item;
    }

    private ToolStripMenuItem Toggle(string text, Func<bool> get, Action<bool> set)
    {
        var item = new ToolStripMenuItem(text) { Checked = get(), CheckOnClick = true };
        item.CheckedChanged += (_, _) => { set(item.Checked); SaveConfig(); };
        return item;
    }

    private void ReloadConfig()
    {
        ConfigLoadResult result = AppConfigStore.Load(_logger.Warning);
        _config = result.Config;
        _ocr.ResetEngine();
        RegisterHotKey();
        _notify.ContextMenuStrip = BuildMenu();
        Toast.ShowMessage(result.HadError ? "設定を読み込めませんでした" : "設定を再読み込みしました", result.ErrorMessage, result.HadError);
    }

    private void RegisterHotKey()
    {
        HotKeyGesture gesture = HotKeyParser.Parse(_config.Hotkey);
        if (_hotKey.Register(_window.Handle, gesture))
        {
            _notify.Icon = _appIcon;
            _logger.Info($"ホットキー登録成功: {_config.Hotkey}");
        }
        else
        {
            _notify.Icon = SystemIcons.Error;
            _logger.Warning($"ホットキー登録失敗: {_config.Hotkey}");
            Toast.ShowMessage($"{_config.Hotkey} は他のアプリが使用中です", "ZoomIt が起動していないか確認するか、設定でキーを変更してください", true);
        }
    }

    private void SaveConfig()
    {
        try { AppConfigStore.Save(_config); }
        catch (Exception ex) { _logger.Error("設定保存失敗", ex); Toast.ShowMessage("設定を保存できませんでした", ex.Message, true); }
    }
    private static void OpenLanguageSettings() => Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true });
    private static void OpenPpOcrV6Docs() => Process.Start(new ProcessStartInfo("https://rapidai.github.io/RapidOCRDocs/main/install_usage/rapidocr/usage/") { UseShellExecute = true });
    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private static Icon LoadAppIcon()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ScreenOCR.Resources.ScreenOCR.ico");
        if (stream is null) return (Icon)SystemIcons.Application.Clone();
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
    private void ClearSelection() { _snapshot?.Dispose(); _snapshot = null; _overlay = null; _busy = false; }

    protected override void ExitThreadCore()
    {
        _logger.Info("終了");
        _hotKey.Dispose();
        _window.Dispose();
        _notify.Visible = false;
        _notify.Dispose();
        _appIcon.Dispose();
        ClearSelection();
        base.ExitThreadCore();
    }

    private sealed class HotKeyWindow : NativeWindow, IDisposable
    {
        private readonly Action _callback;
        public HotKeyWindow(Action callback) { _callback = callback; CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message m) { if (m.Msg == Native.WmHotKey) _callback(); base.WndProc(ref m); }
        public void Dispose() => DestroyHandle();
    }
}
