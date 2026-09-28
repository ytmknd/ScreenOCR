using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ScreenOCR;

public enum OcrFailure { None, NoRecognizerAvailable, NoTextFound, Timeout, ExternalEngineError }
public sealed record OcrLanguageInfo(string Tag, string DisplayName);
public sealed record OcrAttemptInfo(
    string Name, double Scale, bool Inverted, bool Rotated,
    int ValidChars, int ImplausibleChars, double Score, long ElapsedMilliseconds);
public sealed record OcrPipelineResult(
    OcrFailure Failure, string Text, string RawText, string LanguageTag, double Scale,
    int ValidChars, int ImplausibleChars, double Score,
    string SelectedAttempt, IReadOnlyList<OcrAttemptInfo> Attempts, IReadOnlyList<OcrLineInfo> Lines,
    long ElapsedMilliseconds, OcrMergeSummary? MergeInfo, string? ErrorMessage = null);

/// <summary>設定で指定された言語キー（"auto" や BCP-47 タグ）に対して実際に解決されたエンジン。</summary>
public sealed record ResolvedOcrEngine(string ConfiguredKey, string LanguageTag, OcrEngine Engine);

public sealed class OcrService
{
    private readonly Dictionary<string, OcrEngine> _engines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _resolvedTags = new(StringComparer.OrdinalIgnoreCase);

    public static int MaxImageDimension => checked((int)OcrEngine.MaxImageDimension);

    public static IReadOnlyList<OcrLanguageInfo> GetAvailableLanguages() => OcrEngine.AvailableRecognizerLanguages
        .Select(x => new OcrLanguageInfo(x.LanguageTag, x.DisplayName)).ToList();

    public void ResetEngine()
    {
        _engines.Clear();
        _resolvedTags.Clear();
    }

    public OcrEngine? CreateEngine(string configuredLanguage, out string selectedLanguage)
    {
        string key = string.IsNullOrWhiteSpace(configuredLanguage) ? "auto" : configuredLanguage;
        if (_engines.TryGetValue(key, out OcrEngine? cached))
        {
            selectedLanguage = _resolvedTags.GetValueOrDefault(key, string.Empty);
            return cached;
        }

        OcrEngine? engine = null;
        IReadOnlyList<Language> available = OcrEngine.AvailableRecognizerLanguages.ToList();
        if (!key.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            try { engine = OcrEngine.TryCreateFromLanguage(new Language(key)); }
            catch (Exception) { engine = null; }
        }

        if (key.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
            Language? japanese = available.FirstOrDefault(x => x.LanguageTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase));
            if (engine is null && japanese is not null) engine = OcrEngine.TryCreateFromLanguage(japanese);
            if (engine is null && available.Count > 0) engine = OcrEngine.TryCreateFromLanguage(available[0]);
        }

        string tag = engine?.RecognizerLanguage.LanguageTag ?? string.Empty;
        if (engine is not null)
        {
            _engines[key] = engine;
            _resolvedTags[key] = tag;
        }
        selectedLanguage = tag;
        return engine;
    }

    /// <summary>
    /// 設定の <see cref="AppConfig.EffectiveOcrLanguages"/> を順に解決する。同じ言語タグに解決された
    /// 重複（例: "auto" と "ja" が両方 ja に解決される）は 1 つにまとめる。
    /// 利用可能な言語が 1 つしかない、あるいは 2 番目以降の言語がこの PC に無い場合は
    /// 結果が 1 件だけになる。呼び出し側はこの件数でマージの可否を判断する（統合の自動無効化）。
    /// </summary>
    public IReadOnlyList<ResolvedOcrEngine> ResolveEngines(AppConfig config)
    {
        var results = new List<ResolvedOcrEngine>();
        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string configured in config.EffectiveOcrLanguages)
        {
            OcrEngine? engine = CreateEngine(configured, out string tag);
            if (engine is null || string.IsNullOrEmpty(tag)) continue;
            if (!seenTags.Add(tag)) continue;
            results.Add(new ResolvedOcrEngine(configured, tag, engine));
        }
        return results;
    }

    public async Task<OcrPipelineResult> RecognizeAsync(Bitmap source, AppConfig config, float dpiPercent = 100f, bool raw = false, CancellationToken cancellationToken = default)
    {
        if (config.OcrEngine.Equals(OcrBackendNames.PpOcrV6Small, StringComparison.OrdinalIgnoreCase))
            return await new PpOcrV6SmallService().RecognizeAsync(source, config, raw, cancellationToken);

        var total = Stopwatch.StartNew();
        IReadOnlyList<ResolvedOcrEngine> resolved = ResolveEngines(config);
        if (resolved.Count == 0) return Failure(OcrFailure.NoRecognizerAvailable, total.ElapsedMilliseconds);

        OcrEngine engine = resolved[0].Engine;
        string language = resolved[0].LanguageTag;

        IReadOnlyList<double> scales = ImagePreprocessor.GetCandidateScales(
            source.Width, source.Height, config.MaxScale, MaxImageDimension, dpiPercent);
        if (scales.Count == 0) return Failure(OcrFailure.NoTextFound, total.ElapsedMilliseconds);

        double scaleA = scales[0];
        bool dark = config.AutoInvert && ImagePreprocessor.CalculateAverageLuminance(source) < 0.40;
        var attempts = new List<AttemptResult>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(config.AttemptTimeoutMs);

        AttemptResult a = await RunAttemptAsync("A", source, scaleA, false, false, engine, config, timeout.Token);
        attempts.Add(a);
        bool strongA = IsAcceptable(a.Info);

        AttemptResult? b = null;
        if (!strongA && scales.Count >= 2 && total.ElapsedMilliseconds < config.AttemptTimeoutMs)
        {
            b = await RunAttemptAsync("B", source, scales[1], false, false, engine, config, timeout.Token);
            attempts.Add(b);
        }

        if (b is not null && !IsAcceptable(b.Info) && scales.Count >= 3 && total.ElapsedMilliseconds < config.AttemptTimeoutMs)
        {
            attempts.Add(await RunAttemptAsync("C", source, scales[2], false, false, engine, config, timeout.Token));
        }

        if (dark && total.ElapsedMilliseconds < config.AttemptTimeoutMs)
        {
            attempts.Add(await RunAttemptAsync("D", source, scaleA, true, false, engine, config, timeout.Token));
        }

        int bestCount = attempts.Max(x => x.Info.ValidChars);
        if (!strongA && (config.TryVertical || bestCount < 3) && total.ElapsedMilliseconds < config.AttemptTimeoutMs)
        {
            attempts.Add(await RunAttemptAsync("E", source, scaleA, false, true, engine, config, timeout.Token));
        }

        AttemptResult best = attempts.OrderByDescending(x => x.Info.Score).ThenBy(x => x.Order).First();
        if (best.Info.ValidChars == 0)
        {
            return new(OcrFailure.NoTextFound, string.Empty, string.Empty, language, best.Info.Scale,
                best.Info.ValidChars, best.Info.ImplausibleChars, best.Info.Score, best.Info.Name,
                attempts.Select(x => x.Info).ToList(), [], total.ElapsedMilliseconds, null);
        }

        // 倍率探索（試行 A〜E）はベースエンジン（resolved[0]）のみで行う。倍率が確定した後、
        // 統合が有効かつ 2 言語目が実際に利用可能な場合に限り、副エンジンを同じ倍率・変換で
        // 1 回だけ実行する。これにより OCR 呼び出し回数は増えない（通常 2 回、最悪でも 6 回）。
        IReadOnlyList<OcrLineInfo> finalLines = best.Lines;
        OcrMergeSummary mergeInfo;
        if (resolved.Count >= 2 && config.MergeEngines && total.ElapsedMilliseconds < config.AttemptTimeoutMs)
        {
            ResolvedOcrEngine secondary = resolved[1];
            AttemptResult secondaryAttempt = await RunAttemptAsync("S", source, best.Info.Scale, best.Info.Inverted, best.Info.Rotated, secondary.Engine, config, timeout.Token);
            (finalLines, mergeInfo) = OcrResultMerger.Merge(best.Lines, language, secondaryAttempt.Lines, secondary.LanguageTag, config.MergeOptions);
        }
        else
        {
            string reason = resolved.Count < 2
                ? "利用可能な OCR 言語が 1 つのため統合を無効化しました"
                : "設定で統合が無効化されています";
            mergeInfo = OcrMergeSummary.NotAttempted(reason, language);
        }

        string rawText = BuildRawText(finalLines);
        string processed = raw ? rawText : TextPostProcessor.Process(finalLines, config.TextOptions);
        return new(OcrFailure.None, processed, rawText, language, best.Info.Scale,
            best.Info.ValidChars, best.Info.ImplausibleChars, best.Info.Score, best.Info.Name,
            attempts.Select(x => x.Info).ToList(), finalLines, total.ElapsedMilliseconds, mergeInfo);

        static bool IsAcceptable(OcrAttemptInfo attempt) => attempt.ValidChars >= 8 && attempt.ImplausibleChars == 0;
        OcrPipelineResult Failure(OcrFailure failure, long elapsed) =>
            new(failure, string.Empty, string.Empty, string.Empty, 1, 0, 0, 0, string.Empty, [], [], elapsed, null);
    }

    private static string BuildRawText(IReadOnlyList<OcrLineInfo> lines) =>
        string.Join(Environment.NewLine, lines.Select(line => string.Join(" ", line.Words.Select(word => word.Text))));

    private static async Task<AttemptResult> RunAttemptAsync(string name, Bitmap source, double scale, bool invert, bool rotate, OcrEngine engine, AppConfig config, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        using Bitmap scaled = ImagePreprocessor.Resize(source, scale);
        using Bitmap transformed = invert ? ImagePreprocessor.Invert(scaled) : rotate ? ImagePreprocessor.RotateClockwise(scaled) : new Bitmap(scaled);
        if (config.DebugSaveImages)
        {
            string directory = Path.Combine(AppConfigStore.DirectoryPath, "debug");
            Directory.CreateDirectory(directory);
            transformed.Save(Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}.png"), ImageFormat.Png);
        }
        using SoftwareBitmap software = ToSoftwareBitmap(transformed);
        OcrResult result = await engine.RecognizeAsync(software).AsTask(token);
        List<OcrLineInfo> lines = result.Lines.Select(line => new OcrLineInfo(line.Words.Select(word =>
            new OcrWordInfo(word.Text, new RectF((float)word.BoundingRect.X, (float)word.BoundingRect.Y, (float)word.BoundingRect.Width, (float)word.BoundingRect.Height))).ToList())).ToList();
        string rawText = BuildRawText(lines);
        OcrQuality quality = OcrQualityScorer.Evaluate(rawText, config.ScoreWeights);
        int order = name[0] - 'A';
        return new AttemptResult(order, new OcrAttemptInfo(name, scale, invert, rotate,
            quality.ValidChars, quality.ImplausibleChars, quality.Score, timer.ElapsedMilliseconds), rawText, lines);
    }

    private static SoftwareBitmap ToSoftwareBitmap(Bitmap source)
    {
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        // 画素を 1:1 で写す。DrawImageUnscaled は画像とキャンバスの解像度差ぶん拡大縮小するため、
        // 96 dpi の前処理画像と画面 DPI のキャンバスが噛み合うと黙って切り落とされる。
        using (Graphics graphics = Graphics.FromImage(bitmap))
            graphics.DrawImage(source, rectangle, rectangle, GraphicsUnit.Pixel);
        BitmapData data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowSize = bitmap.Width * 4;
            byte[] pixels = new byte[rowSize * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
            {
                IntPtr row = data.Scan0 + y * data.Stride;
                Marshal.Copy(row, pixels, y * rowSize, rowSize);
                for (int x = y * rowSize + 3; x < (y + 1) * rowSize; x += 4) pixels[x] = 255;
            }
            return SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, bitmap.Width, bitmap.Height, BitmapAlphaMode.Premultiplied);
        }
        finally { bitmap.UnlockBits(data); }
    }

    private sealed record AttemptResult(int Order, OcrAttemptInfo Info, string RawText, IReadOnlyList<OcrLineInfo> Lines);
}
