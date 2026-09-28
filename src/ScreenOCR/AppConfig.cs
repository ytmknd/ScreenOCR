using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ScreenOCR;

public sealed class ReplacementRule
{
    public string Pattern { get; set; } = string.Empty;
    public string Replacement { get; set; } = string.Empty;
    public bool Regex { get; set; }
    public bool Enabled { get; set; } = true;
    [JsonIgnore] public bool IsValid { get; private set; } = true;

    public void Validate(Action<string>? warning = null)
    {
        IsValid = true;
        if (!Regex) return;
        try { _ = new Regex(Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200)); }
        catch (ArgumentException ex)
        {
            IsValid = false;
            warning?.Invoke($"不正な置換正規表現を無効化しました: {Pattern} ({ex.Message})");
        }
    }

    public string Apply(string input)
    {
        if (!Enabled || !IsValid) return input;
        try
        {
            return Regex
                ? System.Text.RegularExpressions.Regex.Replace(input, Pattern, Replacement, RegexOptions.None, TimeSpan.FromMilliseconds(200))
                : input.Replace(Pattern, Replacement, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            IsValid = false;
            return input;
        }
    }
}

public sealed class AppConfig
{
    public string Hotkey { get; set; } = "Ctrl+Alt+6";
    /// <summary>使用する OCR バックエンド。"windows" または "ppocrv6-small"。</summary>
    public string OcrEngine { get; set; } = OcrBackendNames.PpOcrV6Small;
    /// <summary>RapidOCR 3.9 以上（PP-OCRv6 Small）を導入した Python 実行ファイル。</summary>
    public string PpOcrV6SmallPythonPath { get; set; } = "python";
    public int PpOcrV6SmallTimeoutMs { get; set; } = 120000;
    public string OcrLanguage { get; set; } = "auto";

    /// <summary>
    /// 順序付きの OCR 言語リスト（先頭がベース優先）。null または空配列のときは
    /// <see cref="OcrLanguage"/>（単数、後方互換）にフォールバックする。§9.2 の既定値
    /// <c>["ja", "en-US"]</c> は新規インストール時のみ <see cref="AppConfigStore.Load"/> が書き込む。
    /// 既存の config.json に <c>ocrLanguages</c> キーが無い場合は「従来どおり」単一言語で動作する。
    /// </summary>
    public List<string>? OcrLanguages { get; set; }

    /// <summary>2 つ以上の言語が実際に利用可能なときに、両エンジンの結果を統合するかどうか。</summary>
    public bool MergeEngines { get; set; } = true;

    /// <summary>
    /// マージ時、対抗候補が採用されるために必要なスコア差。スコアは文字ごとの重み付け（M-2 対応）に
    /// なったため小数（既定値は実測に基づき 0.3。SPEC §1.7 の実測例では base と candidate の差が
    /// 約 0.4 だったため、これより小さい値にしないと正しい置換が margin に阻まれてしまう）。
    /// </summary>
    public double MergeMarginScore { get; set; } = 0.3;

    /// <summary>
    /// ベース選択時、先頭言語（通常 ja）の結果の CJK 文字比率がこの値以上ならベースとして採用する
    /// 閾値（暫定値。実測で調整する）。
    /// </summary>
    public double MergeBaseCjkRatio { get; set; } = 0.30;

    /// <summary>
    /// マージ時、候補語（他エンジンの単語）の矩形がベースランの矩形を「実質的に覆っている」と
    /// みなす面積比（交差面積 / 小さい方の面積）の閾値。M-1 対応: 1 つの候補語が複数の狭い
    /// ベースランに跨って重なる場合、この閾値を満たすランをまとめて 1 グループとして置換する。
    /// </summary>
    public double MergeCoverageAreaRatio { get; set; } = 0.5;

    /// <summary>
    /// 修正 B（SPEC §1.8）: 通常のマージン判定（<see cref="MergeMarginScore"/>）では決着しない
    /// 実質同点（|CandidateScore - BaseScore| がこの値未満）の場合に、字種による判定を行う対象と
    /// みなすしきい値。実測（"Windows11" vs "Windows 11" 等がスコア同点だった）に基づき
    /// <see cref="MergeMarginScore"/> と同じ既定値 0.3 とした。
    /// </summary>
    public double MergeTieBreakMarginScore { get; set; } = 0.3;

    /// <summary>
    /// 修正 B（SPEC §1.8）: 実質同点時、ベースラン文字列の CJK 文字比率がこの値未満なら
    /// 「ラテン文字・数字主体」とみなしセカンダリ候補を採用する。この値以上なら CJK 主体として
    /// ベースを維持する。
    /// </summary>
    public double MergeTieBreakCjkRatio { get; set; } = 0.3;

    /// <summary>常用漢字外の CJK 統合漢字 1 文字あたりの減点（暫定値）。</summary>
    public int ScoreRareKanjiPenalty { get; set; } = 4;
    /// <summary>ラテン文字と CJK 統合漢字が同居するトークン 1 個あたりの減点（暫定値）。</summary>
    public int ScoreMixedScriptPenalty { get; set; } = 6;
    /// <summary>CJK に挟まれた孤立ラテン 1 文字あたりの減点（暫定値）。</summary>
    public int ScoreIsolatedLatinPenalty { get; set; } = 2;
    /// <summary>英単語辞書にヒットしたトークン 1 個あたりの加点（暫定値）。</summary>
    public int ScoreEnglishLexiconBonus { get; set; } = 2;

    /// <summary>
    /// 基礎点の文字重み（M-2 対応）: 常用漢字・かな・約物など、常用外CJK・ラテン・数字のいずれにも
    /// 該当しない妥当な文字 1 個あたりの基礎点。
    /// </summary>
    public double ScorePlausibleCharWeight { get; set; } = 1.0;
    /// <summary>基礎点の文字重み: 英単語辞書にヒットしたラテン文字ラン内の 1 文字あたりの基礎点。</summary>
    public double ScoreLatinLexiconCharWeight { get; set; } = 1.0;
    /// <summary>基礎点の文字重み: 英単語辞書にヒットしなかったラテン文字ラン内の 1 文字あたりの基礎点。</summary>
    public double ScoreLatinNonLexiconCharWeight { get; set; } = 0.3;
    /// <summary>基礎点の文字重み: 数字 1 文字あたりの基礎点。</summary>
    public double ScoreDigitCharWeight { get; set; } = 0.5;

    /// <summary>
    /// 選択範囲を OCR へ渡す前に QR コードを探すかどうか。見つかればその内容をコピーして OCR は行わず、
    /// 見つからなければ従来どおり OCR へ進む（<see cref="QrCodeReader"/>）。
    /// </summary>
    public bool QrCodeEnabled { get; set; } = true;

    public int OverlayDimPercent { get; set; } = 35;
    public string SelectionBorderColor { get; set; } = "#0078D4";
    public int MinSelectionSize { get; set; } = 5;
    public bool AutoInvert { get; set; } = true;
    public bool TryVertical { get; set; }
    public double MaxScale { get; set; } = 4.0;
    public int AttemptTimeoutMs { get; set; } = 2000;
    public bool RemoveCjkSpaces { get; set; } = true;
    public bool SortLines { get; set; } = true;
    public bool JoinLines { get; set; }
    public bool CollapseSpaces { get; set; } = true;
    public bool NormalizeFullWidth { get; set; }
    public bool HalfToFullKana { get; set; }
    public List<ReplacementRule> Replacements { get; set; } = [];
    public bool CopyImageToo { get; set; }
    public bool RunAtStartup { get; set; }
    public bool DebugSaveImages { get; set; }
    public int LogRetentionDays { get; set; } = 7;

    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    [JsonIgnore] public TextProcessingOptions TextOptions => new()
    {
        RemoveCjkSpaces = RemoveCjkSpaces, SortLines = SortLines, JoinLines = JoinLines,
        CollapseSpaces = CollapseSpaces, NormalizeFullWidth = NormalizeFullWidth,
        HalfToFullKana = HalfToFullKana, Replacements = Replacements
    };

    /// <summary>
    /// 実際に使う言語リスト。<see cref="OcrLanguages"/> が非空ならそれを、そうでなければ
    /// <see cref="OcrLanguage"/>（単数）を 1 要素のリストとして返す（後方互換）。
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveOcrLanguages =>
        OcrLanguages is { Count: > 0 } ? OcrLanguages : [string.IsNullOrWhiteSpace(OcrLanguage) ? "auto" : OcrLanguage];

    [JsonIgnore]
    public OcrScoreWeights ScoreWeights => new(ScoreRareKanjiPenalty, ScoreMixedScriptPenalty, ScoreIsolatedLatinPenalty, ScoreEnglishLexiconBonus,
        ScorePlausibleCharWeight, ScoreLatinLexiconCharWeight, ScoreLatinNonLexiconCharWeight, ScoreDigitCharWeight);

    [JsonIgnore]
    public OcrMergeOptions MergeOptions => new(ScoreWeights, MergeMarginScore, MergeBaseCjkRatio, MergeCoverageAreaRatio,
        MergeTieBreakMarginScore, MergeTieBreakCjkRatio);

    public void Validate(Action<string>? warning = null)
    {
        OcrEngine = (OcrEngine ?? string.Empty).Trim().ToLowerInvariant();
        if (OcrEngine is not (OcrBackendNames.Windows or OcrBackendNames.PpOcrV6Small))
        {
            warning?.Invoke($"不正な OCR エンジンを既定値へ戻しました: {OcrEngine}");
            OcrEngine = OcrBackendNames.PpOcrV6Small;
        }
        PpOcrV6SmallPythonPath = string.IsNullOrWhiteSpace(PpOcrV6SmallPythonPath) ? "python" : PpOcrV6SmallPythonPath.Trim();
        if (!HotKeyParser.TryParse(Hotkey, out _))
        {
            warning?.Invoke($"不正なホットキーを既定値へ戻しました: {Hotkey}");
            Hotkey = "Ctrl+Alt+6";
        }
        OverlayDimPercent = Math.Clamp(OverlayDimPercent, 0, 90);
        MinSelectionSize = Math.Clamp(MinSelectionSize, 1, 100);
        MaxScale = Math.Clamp(MaxScale, 1, 8);
        AttemptTimeoutMs = Math.Clamp(AttemptTimeoutMs, 250, 30000);
        PpOcrV6SmallTimeoutMs = Math.Clamp(PpOcrV6SmallTimeoutMs, 1000, 1800000);
        LogRetentionDays = Math.Clamp(LogRetentionDays, 1, 365);
        MergeMarginScore = Math.Clamp(MergeMarginScore, 0.0, 100.0);
        MergeBaseCjkRatio = Math.Clamp(MergeBaseCjkRatio, 0.0, 1.0);
        MergeCoverageAreaRatio = Math.Clamp(MergeCoverageAreaRatio, 0.0, 1.0);
        MergeTieBreakMarginScore = Math.Clamp(MergeTieBreakMarginScore, 0.0, 100.0);
        MergeTieBreakCjkRatio = Math.Clamp(MergeTieBreakCjkRatio, 0.0, 1.0);
        ScoreRareKanjiPenalty = Math.Clamp(ScoreRareKanjiPenalty, 0, 1000);
        ScoreMixedScriptPenalty = Math.Clamp(ScoreMixedScriptPenalty, 0, 1000);
        ScoreIsolatedLatinPenalty = Math.Clamp(ScoreIsolatedLatinPenalty, 0, 1000);
        ScoreEnglishLexiconBonus = Math.Clamp(ScoreEnglishLexiconBonus, 0, 1000);
        ScorePlausibleCharWeight = Math.Clamp(ScorePlausibleCharWeight, 0.0, 10.0);
        ScoreLatinLexiconCharWeight = Math.Clamp(ScoreLatinLexiconCharWeight, 0.0, 10.0);
        ScoreLatinNonLexiconCharWeight = Math.Clamp(ScoreLatinNonLexiconCharWeight, 0.0, 10.0);
        ScoreDigitCharWeight = Math.Clamp(ScoreDigitCharWeight, 0.0, 10.0);
        if (OcrLanguages is not null)
        {
            OcrLanguages = OcrLanguages.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        foreach (ReplacementRule rule in Replacements) rule.Validate(warning);
    }
}

public sealed record ConfigLoadResult(AppConfig Config, bool HadError, string? ErrorMessage);

public static class AppConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true
    };
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenOCR");
    public static string FilePath => Path.Combine(DirectoryPath, "config.json");

    public static ConfigLoadResult Load(Action<string>? warning = null)
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!File.Exists(FilePath))
        {
            var initial = new AppConfig { OcrLanguages = ["ja", "en-US"] };
            Save(initial);
            return new(initial, false, null);
        }
        try
        {
            AppConfig config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath, Encoding.UTF8), Options) ?? new();
            config.Validate(warning);
            return new(config, false, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warning?.Invoke($"設定ファイルを読めませんでした。既定値で起動します: {ex.Message}");
            return new(new AppConfig(), true, ex.Message);
        }
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(DirectoryPath);
        config.Validate();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(config, Options), new UTF8Encoding(false));
    }
}
