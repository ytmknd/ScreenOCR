using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ScreenOCR;

public sealed record PpOcrV6SmallAvailability(bool Available, string? Command, string? Version, string? ErrorMessage);

/// <summary>RapidOCR の ONNX Runtime バックエンドで PP-OCRv6 Small を実行する。</summary>
public sealed class PpOcrV6SmallService
{
    private const string ScriptResourceName = "ScreenOCR.Scripts.ppocrv6_small.py";

    /// <summary>検出（DB）→ 認識の通常経路。</summary>
    private const string DetectAttemptName = "PPv6";

    /// <summary>検出が空だったため、切り抜き全体を 1 行として認識モデルへ渡した経路（§6.7.1）。</summary>
    private const string RecOnlyAttemptName = "PPv6-rec";

    public static PpOcrV6SmallAvailability GetAvailability(AppConfig config)
    {
        if (!TryResolvePython(config.PpOcrV6SmallPythonPath, out string? python, out string? error))
            return new(false, null, null, error);

        var info = CreateProcessInfo(python);
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("import importlib.metadata as m; print(m.version('rapidocr'))");
        try
        {
            using var process = Process.Start(info);
            if (process is null) return new(false, python, null, "Python を起動できませんでした。");
            if (!process.WaitForExit(5000))
            {
                TryKill(process);
                return new(false, python, null, "RapidOCR の確認がタイムアウトしました。");
            }
            string stdout = process.StandardOutput.ReadToEnd().Trim();
            string stderr = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode != 0)
                return new(false, python, null, $"RapidOCR 3.9 以上がありません: {FirstNonEmpty(stderr, stdout)}");
            if (!Version.TryParse(stdout, out Version? version) || version < new Version(3, 9))
                return new(false, python, stdout, $"RapidOCR 3.9 以上が必要です（検出: {stdout}）。");
            return new(true, python, stdout, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new(false, python, null, $"Python を起動できませんでした: {ex.Message}");
        }
    }

    public async Task<OcrPipelineResult> RecognizeAsync(
        Bitmap source, AppConfig config, bool raw = false, CancellationToken cancellationToken = default)
    {
        var total = Stopwatch.StartNew();
        PpOcrV6SmallAvailability availability = GetAvailability(config);
        if (!availability.Available || availability.Command is null)
            return Failure(OcrFailure.NoRecognizerAvailable, total.ElapsedMilliseconds, availability.ErrorMessage);

        string workDirectory = Path.Combine(Path.GetTempPath(), "ScreenOCR", "PP-OCRv6-Small", Guid.NewGuid().ToString("N"));
        string inputPath = Path.Combine(workDirectory, "screenocr-input.png");
        string scriptPath = Path.Combine(workDirectory, "ppocrv6_small.py");
        string outputPath = Path.Combine(workDirectory, "result.json");
        Directory.CreateDirectory(workDirectory);

        try
        {
            source.Save(inputPath, ImageFormat.Png);
            await ExtractScriptAsync(scriptPath, cancellationToken);
            using var process = new Process { StartInfo = BuildStartInfo(availability.Command, scriptPath, inputPath, outputPath) };
            try
            {
                if (!process.Start())
                    return Failure(OcrFailure.ExternalEngineError, total.ElapsedMilliseconds, "PP-OCRv6 Small を起動できませんでした。");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                return Failure(OcrFailure.NoRecognizerAvailable, total.ElapsedMilliseconds,
                    $"PP-OCRv6 Small を起動できませんでした: {ex.Message}");
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(config.PpOcrV6SmallTimeoutMs);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return Failure(OcrFailure.Timeout, total.ElapsedMilliseconds,
                    $"PP-OCRv6 Small が {config.PpOcrV6SmallTimeoutMs} ms 以内に完了しませんでした。");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                string details = FirstNonEmpty(stderr, stdout, $"終了コード {process.ExitCode}");
                return Failure(OcrFailure.ExternalEngineError, total.ElapsedMilliseconds,
                    $"PP-OCRv6 Small の実行に失敗しました: {Limit(details, 1000)}");
            }
            if (!File.Exists(outputPath))
            {
                string details = FirstNonEmpty(stderr, stdout, "JSON 結果が生成されませんでした。");
                return Failure(OcrFailure.ExternalEngineError, total.ElapsedMilliseconds,
                    $"PP-OCRv6 Small の結果を取得できませんでした: {Limit(details, 1000)}");
            }

            try
            {
                string json = await File.ReadAllTextAsync(outputPath, Encoding.UTF8, cancellationToken);
                return ParseResult(json, config, raw, total.ElapsedMilliseconds);
            }
            catch (JsonException ex)
            {
                return Failure(OcrFailure.ExternalEngineError, total.ElapsedMilliseconds,
                    $"PP-OCRv6 Small の JSON 結果を解析できませんでした: {ex.Message}");
            }
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    public static OcrPipelineResult ParseResult(string json, AppConfig config, bool raw = false, long elapsedMilliseconds = 0)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("lines", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            throw new JsonException("lines 配列がありません。");

        var lines = new List<OcrLineInfo>();
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("text", out JsonElement textElement) || textElement.ValueKind != JsonValueKind.String) continue;
            string text = textElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) continue;
            lines.Add(new OcrLineInfo([new OcrWordInfo(text, ReadBounds(item))]));
        }

        string attemptName = ReadMode(document.RootElement) == "rec-only" ? RecOnlyAttemptName : DetectAttemptName;
        double scale = ReadScale(document.RootElement);
        string rawText = string.Join(Environment.NewLine, lines.Select(x => x.Words[0].Text));
        OcrQuality quality = OcrQualityScorer.Evaluate(rawText, config.ScoreWeights);
        var attempt = new OcrAttemptInfo(attemptName, scale, false, false, quality.ValidChars,
            quality.ImplausibleChars, quality.Score, elapsedMilliseconds);
        OcrMergeSummary merge = OcrMergeSummary.NotAttempted("PP-OCRv6 Small 単独実行", "ja+en");
        if (lines.Count == 0 || quality.ValidChars == 0)
        {
            return new(OcrFailure.NoTextFound, string.Empty, rawText, "ja+en", scale,
                quality.ValidChars, quality.ImplausibleChars, quality.Score, attemptName, [attempt], lines,
                elapsedMilliseconds, merge);
        }

        string processed = raw ? rawText : TextPostProcessor.Process(lines, config.TextOptions);
        return new(OcrFailure.None, processed, rawText, "ja+en", scale,
            quality.ValidChars, quality.ImplausibleChars, quality.Score, attemptName, [attempt], lines,
            elapsedMilliseconds, merge);
    }

    private static async Task ExtractScriptAsync(string path, CancellationToken cancellationToken)
    {
        await using Stream? input = Assembly.GetExecutingAssembly().GetManifestResourceStream(ScriptResourceName);
        if (input is null) throw new InvalidOperationException("PP-OCRv6 Small 実行スクリプトが見つかりません。");
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static ProcessStartInfo BuildStartInfo(string python, string scriptPath, string inputPath, string outputPath)
    {
        ProcessStartInfo info = CreateProcessInfo(python);
        info.WorkingDirectory = Path.GetDirectoryName(scriptPath)!;
        info.ArgumentList.Add(scriptPath);
        info.ArgumentList.Add("--input");
        info.ArgumentList.Add(inputPath);
        info.ArgumentList.Add("--output");
        info.ArgumentList.Add(outputPath);
        return info;
    }

    private static ProcessStartInfo CreateProcessInfo(string python) => new()
    {
        FileName = python,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };

    private static bool TryResolvePython(string configured, [NotNullWhen(true)] out string? path, out string? error)
    {
        string candidate = Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(configured) ? "python" : configured.Trim());
        if (File.Exists(candidate))
        {
            path = Path.GetFullPath(candidate);
            error = null;
            return true;
        }
        if (Path.IsPathRooted(candidate) || candidate.Contains(Path.DirectorySeparatorChar) || candidate.Contains(Path.AltDirectorySeparatorChar))
        {
            path = null;
            error = $"Python が見つかりません: {candidate}";
            return false;
        }

        string[] extensions = Path.HasExtension(candidate)
            ? [string.Empty]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string extension in extensions)
            {
                try
                {
                    string item = Path.Combine(directory.Trim().Trim('"'), candidate + extension.ToLowerInvariant());
                    if (!File.Exists(item)) continue;
                    path = Path.GetFullPath(item);
                    error = null;
                    return true;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        path = null;
        error = $"Python が見つかりません: {candidate}";
        return false;
    }

    private static string ReadMode(JsonElement root) =>
        root.TryGetProperty("mode", out JsonElement mode) && mode.ValueKind == JsonValueKind.String
            ? mode.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>スクリプトが検出前に掛けた拡大率。ログ・診断用で、座標は元の切り抜き基準に戻っている。</summary>
    private static double ReadScale(JsonElement root) =>
        root.TryGetProperty("scale", out JsonElement scale) && scale.ValueKind == JsonValueKind.Number
            && scale.TryGetDouble(out double value) && value > 0
            ? value
            : 1;

    private static RectF ReadBounds(JsonElement item)
    {
        if (!item.TryGetProperty("boundingBox", out JsonElement box) || box.ValueKind != JsonValueKind.Array) return default;
        var points = new List<(float X, float Y)>();
        foreach (JsonElement point in box.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array) continue;
            JsonElement[] values = point.EnumerateArray().Take(2).ToArray();
            if (values.Length != 2 || !TryReadSingle(values[0], out float x) || !TryReadSingle(values[1], out float y)) continue;
            points.Add((x, y));
        }
        if (points.Count == 0) return default;
        float left = points.Min(x => x.X), top = points.Min(x => x.Y);
        float right = points.Max(x => x.X), bottom = points.Max(x => x.Y);
        return new(left, top, right - left, bottom - top);
    }

    private static bool TryReadSingle(JsonElement element, out float value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out value)) return true;
        return float.TryParse(element.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static OcrPipelineResult Failure(OcrFailure failure, long elapsed, string? error) =>
        new(failure, string.Empty, string.Empty, "ja+en", 1, 0, 0, 0, DetectAttemptName, [], [], elapsed, null, error);

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static string Limit(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength] + "…";
}
