using System.Text;
using System.Text.Json;

namespace ScreenOCR;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(Native.DpiAwarenessContextPerMonitorAwareV2);
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length > 0) return RunCliAsync(args).GetAwaiter().GetResult();

        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, @"Global\ScreenOCR.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Toast.ShowStandalone("ScreenOCR は既に起動しています");
            return 0;
        }
        Application.Run(new TrayApp());
        return 0;
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--version") { Console.WriteLine("ScreenOCR 1.0.0"); return 0; }
            if (args.Length == 1 && args[0] == "--list-engines")
            {
                AppConfig engineConfig;
                try { engineConfig = File.Exists(AppConfigStore.FilePath) ? AppConfigStore.Load().Config : new AppConfig(); }
                catch (UnauthorizedAccessException) { engineConfig = new AppConfig(); }
                PpOcrV6SmallAvailability pp = PpOcrV6SmallService.GetAvailability(engineConfig);
                Console.WriteLine("windows (available)");
                Console.WriteLine(pp.Available
                    ? $"ppocrv6-small (available: rapidocr {pp.Version}; {pp.Command})"
                    : $"ppocrv6-small (unavailable: {pp.ErrorMessage})");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--list-languages")
            {
                foreach (OcrLanguageInfo item in OcrService.GetAvailableLanguages()) Console.WriteLine($"{item.Tag} ({item.DisplayName})");
                Console.WriteLine($"MaxImageDimension: {OcrService.MaxImageDimension}");
                return 0;
            }

            ValidateOcrArguments(args);

            bool raw = args.Contains("--raw", StringComparer.Ordinal);
            bool jsonVerbose = args.Contains("--json-verbose", StringComparer.Ordinal);
            bool json = jsonVerbose || args.Contains("--json", StringComparer.Ordinal);
            bool noMerge = args.Contains("--no-merge", StringComparer.Ordinal);
            if (args.Contains("--qr", StringComparer.Ordinal))
            {
                using Bitmap qrSource = LoadCliBitmap(args, out _);
                QrScanResult qr = QrCodeReader.Scan(qrSource);
                if (json) Console.WriteLine(JsonSerializer.Serialize(qr, new JsonSerializerOptions { WriteIndented = true }));
                else if (qr.Found) Console.WriteLine(qr.Text);
                else Console.Error.WriteLine("QR コードが見つかりませんでした");
                return qr.Found ? 0 : 1;
            }
            string? language = ValueAfter(args, "--lang");
            string? engine = ValueAfter(args, "--engine");
            string? ppPython = ValueAfter(args, "--ppocr-python");
            AppConfig config;
            try { config = File.Exists(AppConfigStore.FilePath) ? AppConfigStore.Load().Config : new AppConfig(); }
            catch (UnauthorizedAccessException) { config = new AppConfig(); }
            if (!string.IsNullOrWhiteSpace(language))
            {
                string[] parts = language.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) throw new ArgumentException("--lang の値が空です。");
                config.OcrLanguages = parts.ToList();
                config.OcrLanguage = parts[0];
            }
            if (!string.IsNullOrWhiteSpace(engine))
            {
                string normalizedEngine = engine.Trim().ToLowerInvariant();
                if (normalizedEngine is not (OcrBackendNames.Windows or OcrBackendNames.PpOcrV6Small))
                    throw new ArgumentException("--engine は windows または ppocrv6-small を指定してください。");
                config.OcrEngine = normalizedEngine;
            }
            if (!string.IsNullOrWhiteSpace(ppPython)) config.PpOcrV6SmallPythonPath = ppPython;
            config.Validate();
            if (noMerge) config.MergeEngines = false;
            using Bitmap bitmap = LoadCliBitmap(args, out string command);
            var service = new OcrService();
            OcrPipelineResult result = await service.RecognizeAsync(bitmap, config, 100, raw);
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            if (jsonVerbose) Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
            else if (json) Console.WriteLine(JsonSerializer.Serialize(CliResultSummary.From(result), jsonOptions));
            else if (result.Failure == OcrFailure.None) Console.WriteLine(result.Text);
            else Console.Error.WriteLine(!string.IsNullOrWhiteSpace(result.ErrorMessage) ? result.ErrorMessage : result.Failure switch
            {
                OcrFailure.NoRecognizerAvailable => "OCR 言語がインストールされていません",
                OcrFailure.NoTextFound => "文字を認識できませんでした",
                OcrFailure.Timeout => "OCR 処理がタイムアウトしました",
                OcrFailure.ExternalEngineError => "外部 OCR エンジンの実行に失敗しました",
                _ => $"OCR に失敗しました ({command})"
            });
            return result.Failure switch { OcrFailure.None => 0, OcrFailure.NoTextFound => 1, OcrFailure.NoRecognizerAvailable => 2, _ => 4 };
        }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); PrintUsage(); return 3; }
        catch (Exception ex) { Console.Error.WriteLine($"エラー: {ex.Message}"); return 4; }
    }

    private static Bitmap LoadCliBitmap(string[] args, out string command)
    {
        string? file = ValueAfter(args, "--ocr-file");
        if (file is not null)
        {
            command = "--ocr-file";
            if (!File.Exists(file)) throw new ArgumentException($"画像ファイルが見つかりません: {file}");
            using var loaded = new Bitmap(file);
            return new Bitmap(loaded);
        }
        string? regionValue = ValueAfter(args, "--region");
        if (regionValue is not null)
        {
            command = "--region";
            string[] values = regionValue.Split(',', StringSplitOptions.TrimEntries);
            if (values.Length != 4 || !values.All(x => int.TryParse(x, out _))) throw new ArgumentException("--region は x,y,w,h 形式で指定してください。");
            int[] parts = values.Select(int.Parse).ToArray();
            if (parts[2] <= 0 || parts[3] <= 0) throw new ArgumentException("--region の幅と高さは正数にしてください。");
            using FrozenSnapshot snapshot = ScreenCapture.CaptureVirtualDesktop();
            return snapshot.Crop(new Rectangle(parts[0], parts[1], parts[2], parts[3]));
        }
        throw new ArgumentException("--ocr-file または --region を指定してください。");
    }

    private static string? ValueAfter(string[] args, string option)
    {
        int index = Array.IndexOf(args, option);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"{option} の値がありません。");
        return args[index + 1];
    }

    private static void ValidateOcrArguments(string[] args)
    {
        var valueOptions = new HashSet<string>(StringComparer.Ordinal) { "--ocr-file", "--region", "--lang", "--engine", "--ppocr-python" };
        var flagOptions = new HashSet<string>(StringComparer.Ordinal) { "--raw", "--json", "--json-verbose", "--no-merge", "--qr" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i];
            if (!valueOptions.Contains(option) && !flagOptions.Contains(option)) throw new ArgumentException($"不明なオプションです: {option}");
            if (!seen.Add(option)) throw new ArgumentException($"オプションが重複しています: {option}");
            if (valueOptions.Contains(option))
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"{option} の値がありません。");
            }
        }
        bool hasFile = seen.Contains("--ocr-file");
        bool hasRegion = seen.Contains("--region");
        if (hasFile == hasRegion) throw new ArgumentException("--ocr-file または --region のどちらか一方を指定してください。");
        if (hasRegion && seen.Contains("--raw")) throw new ArgumentException("--raw は --ocr-file と一緒に指定してください。");
        if (seen.Contains("--qr"))
        {
            // --qr は OCR を一切走らせないため、OCR 用のオプションは黙って無視せずエラーにする。
            string? conflict = new[] { "--lang", "--engine", "--ppocr-python", "--raw", "--no-merge" }.FirstOrDefault(seen.Contains);
            if (conflict is not null) throw new ArgumentException($"--qr は {conflict} と一緒に指定できません。");
        }
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        "使用法: ScreenOCR --list-engines | --list-languages | --ocr-file <path> [--engine windows|ppocrv6-small] [--ppocr-python <path>] [--lang ja,en-US] [--raw] [--no-merge] [--json | --json-verbose] | --region x,y,w,h [--engine windows|ppocrv6-small] [--ppocr-python <path>] [--lang ja,en-US] [--no-merge] [--json | --json-verbose] | (--ocr-file <path> | --region x,y,w,h) --qr [--json] | --version");
}

/// <summary>
/// <c>--json</c> の出力用サマリー。全単語のバウンディングボックス（<see cref="OcrPipelineResult.Lines"/>）を
/// 含めないことで出力を数十行程度に収める。ボックスが必要な場合は <c>--json-verbose</c> を使う。
/// </summary>
internal sealed record CliResultSummary(
    string Failure, string Text, string RawText, string LanguageTag, double Scale,
    int ValidChars, int ImplausibleChars, double Score, string SelectedAttempt,
    IReadOnlyList<OcrAttemptInfo> Attempts, long ElapsedMilliseconds, OcrMergeSummary? MergeInfo,
    string? ErrorMessage)
{
    public static CliResultSummary From(OcrPipelineResult result) => new(
        result.Failure.ToString(), result.Text, result.RawText, result.LanguageTag, result.Scale,
        result.ValidChars, result.ImplausibleChars, result.Score, result.SelectedAttempt,
        result.Attempts, result.ElapsedMilliseconds, result.MergeInfo, result.ErrorMessage);
}
