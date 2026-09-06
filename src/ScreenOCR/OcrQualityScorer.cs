using System.Globalization;
using System.Reflection;
using System.Text;

namespace ScreenOCR;

public readonly record struct OcrQuality(int ValidChars, int ImplausibleChars, double Score);

/// <summary>
/// 品質スコアの重み。既定値は SPEC §1.7 の実測（日英混在画面向けデュアルエンジン統合）に基づく。
/// <see cref="AppConfig"/> から上書きできる（マジックナンバーをコードへ直書きしないため）。
///
/// スコアは「文字ごとの妥当性による重み付け」を基礎点とする（M-2 対応）。生の文字数
/// (<c>validChars</c>) をそのまま基礎点にすると、情報量の異なる字種（日本語 1 文字 vs ラテン 1 文字）
/// を単純比較できてしまい、ラテン文字のゴミが日本語の正しい認識より高得点になり得る。
/// そこで文字種ごとに <see cref="PlausibleCharWeight"/> / <see cref="LatinLexiconCharWeight"/> /
/// <see cref="LatinNonLexiconCharWeight"/> / <see cref="DigitCharWeight"/> のいずれかの重みを与えて
/// 積算した値（<c>WeightedBase</c>、内部計算）を基礎点とする。常用外 CJK 統合漢字は基礎点を与えず
/// （0 扱い）、従来どおり <see cref="RareKanjiPenalty"/> で別途減点する。
/// </summary>
public readonly record struct OcrScoreWeights(
    int RareKanjiPenalty = 4,
    int MixedScriptTokenPenalty = 6,
    int IsolatedLatinLetterPenalty = 2,
    int EnglishLexiconBonus = 2,
    double PlausibleCharWeight = 1.0,
    double LatinLexiconCharWeight = 1.0,
    double LatinNonLexiconCharWeight = 0.3,
    double DigitCharWeight = 0.5)
{
    // 注意: OcrScoreWeights は record struct なので `new()` は構造体の暗黙のパラメーターなし
    // コンストラクター（全フィールド 0）を呼び出してしまい、上記の既定引数は適用されない。
    // 既定値は必ずここで明示的に指定する。
    public static OcrScoreWeights Default => new(4, 6, 2, 2, 1.0, 1.0, 0.3, 0.5);
}

public static class OcrQualityScorer
{
    private const int ExpectedJoyoKanjiCount = 2136;
    private const int MinimumEnglishLexiconWords = 500;
    private const string JoyoKanjiResourceName = "ScreenOCR.Resources.joyo-kanji.txt";
    private const string EnglishWordsResourceName = "ScreenOCR.Resources.english-words.txt";
    private static readonly HashSet<int> JoyoKanji = LoadJoyoKanji();
    private static readonly HashSet<string> EnglishLexicon = LoadEnglishLexicon();

    public static int JoyoKanjiCount => JoyoKanji.Count;
    public static int EnglishLexiconCount => EnglishLexicon.Count;

    /// <summary>
    /// 全体テキスト用の評価。基礎点は文字ごとの妥当性による重み付け合計（§1.7 M-2 対応）。
    /// 混在スクリプト・孤立ラテン文字の減点、英単語辞書ヒットの加点は既定の重みで考慮する。
    /// </summary>
    public static OcrQuality Evaluate(string text, OcrScoreWeights? weights = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        OcrScoreWeights w = weights ?? OcrScoreWeights.Default;
        Analysis a = Analyze(text, w);
        double penalty = w.RareKanjiPenalty * a.RareKanji
                       + w.MixedScriptTokenPenalty * a.MixedScriptTokens
                       + w.IsolatedLatinLetterPenalty * a.IsolatedLatin;
        double score = a.WeightedBase - penalty + w.EnglishLexiconBonus * a.LexiconHits;
        return new OcrQuality(a.ValidChars, a.RareKanji, score);
    }

    /// <summary>
    /// 短い断片（<see cref="OcrResultMerger"/> がラン単位で比較する際に使う）用の評価。
    /// 減点の合計は基礎点（<c>WeightedBase</c>）を上限としてクランプする。断片は文字数が少なく、
    /// 1 個の混在スクリプト判定だけで大きく負に振れて実質的に無効化されてしまうのを防ぐため。
    /// </summary>
    public static OcrQuality EvaluateFragment(string text, OcrScoreWeights? weights = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        OcrScoreWeights w = weights ?? OcrScoreWeights.Default;
        Analysis a = Analyze(text, w);
        double rawPenalty = w.RareKanjiPenalty * a.RareKanji
                           + w.MixedScriptTokenPenalty * a.MixedScriptTokens
                           + w.IsolatedLatinLetterPenalty * a.IsolatedLatin;
        double cappedPenalty = Math.Min(rawPenalty, a.WeightedBase);
        double score = a.WeightedBase - cappedPenalty + w.EnglishLexiconBonus * a.LexiconHits;
        return new OcrQuality(a.ValidChars, a.RareKanji, score);
    }

    public static bool IsImplausible(Rune rune)
    {
        int value = rune.Value;
        bool isUnifiedIdeograph = value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF;
        return isUnifiedIdeograph && !JoyoKanji.Contains(value);
    }

    private static bool IsUnifiedIdeograph(Rune rune) => rune.Value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF;
    private static bool IsLatinLetter(Rune rune) => rune.Value is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    private readonly record struct Analysis(double WeightedBase, int ValidChars, int RareKanji, int MixedScriptTokens, int IsolatedLatin, int LexiconHits);

    private static Analysis Analyze(string text, OcrScoreWeights w)
    {
        int validChars = 0, rareKanji = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune) || category == UnicodeCategory.Control) continue;
            validChars++;
            if (IsImplausible(rune)) rareKanji++;
        }

        double weightedBase = 0;
        int mixedScriptTokens = 0, isolatedLatin = 0, lexiconHits = 0;
        foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            Rune[] runes = token.EnumerateRunes().ToArray();
            bool hasCjk = false, hasLatin = false;
            foreach (Rune rune in runes)
            {
                if (IsUnifiedIdeograph(rune)) hasCjk = true;
                else if (IsLatinLetter(rune)) hasLatin = true;
            }
            if (hasCjk && hasLatin) mixedScriptTokens++;

            isolatedLatin += CountIsolatedLatinLetters(runes);
            (double tokenWeight, int tokenLexiconHits) = AnalyzeTokenWeight(runes, w);
            weightedBase += tokenWeight;
            lexiconHits += tokenLexiconHits;
        }

        return new Analysis(weightedBase, validChars, rareKanji, mixedScriptTokens, isolatedLatin, lexiconHits);
    }

    /// <summary>CJK 統合漢字に挟まれた、長さ 1 のラテン文字ランを数える（例: "e四代" の "e"）。</summary>
    private static int CountIsolatedLatinLetters(Rune[] runes)
    {
        int count = 0;
        for (int i = 0; i < runes.Length; i++)
        {
            if (!IsLatinLetter(runes[i])) continue;
            bool isRunStart = i == 0 || !IsLatinLetter(runes[i - 1]);
            bool isRunEnd = i == runes.Length - 1 || !IsLatinLetter(runes[i + 1]);
            if (!isRunStart || !isRunEnd) continue; // ラン長が1ではない
            bool prevIsCjk = i > 0 && IsUnifiedIdeograph(runes[i - 1]);
            bool nextIsCjk = i < runes.Length - 1 && IsUnifiedIdeograph(runes[i + 1]);
            if (prevIsCjk || nextIsCjk) count++;
        }
        return count;
    }

    /// <summary>
    /// トークン 1 個分の基礎点（重み付け合計）と英単語辞書ヒット数を同時に計算する。
    /// ラテン文字は連続ラン単位（長さ2以上）で辞書照合し、ヒットしたランは
    /// <see cref="OcrScoreWeights.LatinLexiconCharWeight"/>、ヒットしなければ
    /// <see cref="OcrScoreWeights.LatinNonLexiconCharWeight"/> を各文字に与える。
    /// 数字は <see cref="OcrScoreWeights.DigitCharWeight"/>、常用外 CJK 統合漢字は 0（別途減点）、
    /// それ以外の妥当な文字（常用漢字・かな・約物など）は <see cref="OcrScoreWeights.PlausibleCharWeight"/>。
    /// </summary>
    private static (double Weight, int LexiconHits) AnalyzeTokenWeight(Rune[] runes, OcrScoreWeights w)
    {
        double sum = 0;
        int lexiconHits = 0;
        int i = 0;
        while (i < runes.Length)
        {
            Rune rune = runes[i];
            if (IsLatinLetter(rune))
            {
                int start = i;
                while (i < runes.Length && IsLatinLetter(runes[i])) i++;
                int length = i - start;
                bool hit = false;
                if (length >= 2)
                {
                    string word = string.Concat(runes[start..i].Select(r => r.ToString())).ToLowerInvariant();
                    hit = EnglishLexicon.Contains(word);
                }
                if (hit) lexiconHits++;
                sum += (hit ? w.LatinLexiconCharWeight : w.LatinNonLexiconCharWeight) * length;
                continue;
            }
            if (Rune.IsDigit(rune))
            {
                sum += w.DigitCharWeight;
                i++;
                continue;
            }
            if (IsUnifiedIdeograph(rune) && !JoyoKanji.Contains(rune.Value))
            {
                // 常用外 CJK: 基礎点は与えない（別途 RareKanjiPenalty で減点する）。
                i++;
                continue;
            }
            if (Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                i++;
                continue;
            }
            sum += w.PlausibleCharWeight;
            i++;
        }
        return (sum, lexiconHits);
    }

    private static HashSet<int> LoadJoyoKanji()
    {
        Assembly assembly = typeof(OcrQualityScorer).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(JoyoKanjiResourceName)
            ?? throw new InvalidDataException($"埋め込みリソース {JoyoKanjiResourceName} が見つかりません。");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        string text = reader.ReadToEnd();
        int[] values = text.EnumerateRunes()
            .Where(rune => !Rune.IsWhiteSpace(rune))
            .Select(rune => rune.Value)
            .ToArray();
        var set = values.ToHashSet();
        if (values.Length != ExpectedJoyoKanjiCount || set.Count != ExpectedJoyoKanjiCount)
        {
            throw new InvalidDataException(
                $"常用漢字表は重複なしの {ExpectedJoyoKanjiCount} 字でなければなりません（文字数 {values.Length}、異なり数 {set.Count}）。");
        }
        return set;
    }

    private static HashSet<string> LoadEnglishLexicon()
    {
        Assembly assembly = typeof(OcrQualityScorer).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(EnglishWordsResourceName)
            ?? throw new InvalidDataException($"埋め込みリソース {EnglishWordsResourceName} が見つかりません。");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var set = new HashSet<string>(StringComparer.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string word = line.Trim().ToLowerInvariant();
            if (word.Length > 0) set.Add(word);
        }
        if (set.Count < MinimumEnglishLexiconWords)
        {
            throw new InvalidDataException(
                $"英単語辞書が少なすぎます（{set.Count} 語。最低 {MinimumEnglishLexiconWords} 語が必要です）。");
        }
        return set;
    }
}
