namespace ScreenOCR.Tests;

/// <summary>
/// <see cref="OcrResultMerger"/> の単体テスト。OCR エンジンは使わず、実測データを模した DTO
/// (<see cref="OcrLineInfo"/> / <see cref="OcrWordInfo"/> / <see cref="RectF"/>) だけで検証する。
///
/// フィクスチャは SPEC.md §1.6/§1.7 の実測値（en-US OCR パック導入後、ja/en-US 両エンジンで
/// 実際に認識した結果）に基づく:
///   ja 単体: "画面上の文字を読み取ってクリップボードにコビーします。"
///            "Wlndowsの設定を問いて、言語オプションを確認してください。"
///            "ファイル名:「e四代-2026.x (更新日2026/09/01 )"
///   en-US 単体: "Windows"
///               "774JIZZ: repott_2026.xlsx ( 2026/09/01)"
///
/// 重みが変わると壊れるテストにならないよう、スコアの生値ではなく「どちらが採用されるか」を assert する。
/// </summary>
public sealed class OcrResultMergerTests
{
    private static readonly OcrMergeOptions Options = OcrMergeOptions.Default;

    private static OcrWordInfo Word(string text, float x, float y, float width, float height = 16) => new(text, new RectF(x, y, width, height));
    private static OcrLineInfo Line(params OcrWordInfo[] words) => new(words);

    [Fact]
    public void M1_LatinTypoIsReplacedByCorrectEnglishWord()
    {
        // ja(実測): "Wlndows" + 日本語の続き / en(実測): "Windows"（en はこの行の日本語部分を丸ごと捨てる）。
        var ja = Line(Word("Wlndows", 0, 20, 70), Word("の設定を問いて、言語オプションを確認してください。", 72, 20, 300));
        var en = Line(Word("Windows", 0, 20, 70));

        (IReadOnlyList<OcrLineInfo> lines, OcrMergeSummary summary) = OcrResultMerger.Merge([ja], "ja", [en], "en-US", Options);

        Assert.True(summary.Merged);
        Assert.Equal("Windows", lines[0].Words[0].Text);
        Assert.True(summary.Decisions[0].Replaced);
        // 候補が無かった日本語の続きは変更されない。
        Assert.Equal("の設定を問いて、言語オプションを確認してください。", lines[0].Words[1].Text);
    }

    [Fact]
    public void M1_OverlappingMultiRunCandidateReplacesAllCoveredRunsWithoutLeavingResidue()
    {
        // M-1 の回帰テスト。ja(実測)側は "e" / "四代-2026." / "x (" の 3 ラン（SplitRuns の結果）に
        // 分かれており、en(実測)の 1 語 "repott_2026.xlsx" はこの 3 ランすべてに空間的に重なる
        // （中心点が乗るのは中央のランだけだが、矩形としては前後のランにも実質的に重なっている）。
        // 修正前は中央のランだけが独立に置換され "erepott_2026.xlsxx" のような残骸が残っていた。
        // 修正後は 3 ランがまとめて 1 グループになり、候補語（"repott_2026.xlsx" と、隣接する
        // 独立した候補語 "("）でグループ全体が置換されるため、残骸は出ない。
        //
        // このフラグメント単独だと CJK 比率が低くベース言語選択（primary/secondary の判定）が
        // 崩れてしまうため、実際の画像と同様に強く日本語な行を先頭に足してベースを ja に固定する
        // （実測でも 3 行中 1 行目は純日本語で、その他の行と合わせて判定される）。
        var jaFiller = Line(Word("画面上の文字を読み取ってクリップボードにコビーします。", 0, 0, 400));
        var jaTarget = Line(
            Word("e", 0, 40, 10),
            Word("四代-2026.", 12, 40, 90),
            Word("x", 104, 40, 10),
            Word("(", 116, 40, 10));
        var enTarget = Line(
            Word("repott_2026.xlsx", 4, 40, 130),
            Word("(", 118, 40, 6));

        (IReadOnlyList<OcrLineInfo> lines, OcrMergeSummary summary) =
            OcrResultMerger.Merge([jaFiller, jaTarget], "ja", [enTarget], "en-US", Options);

        Assert.Equal("ja", summary.BaseLanguage);
        // 対象行（2 行目）の 3 ランがまとめて 1 グループとして判定される。
        OcrRunDecision targetDecision = summary.Decisions[^1];
        Assert.True(targetDecision.Replaced);

        string mergedText = string.Join("", lines[1].Words.Select(x => x.Text));
        Assert.Equal("repott_2026.xlsx(", mergedText);
        // 修正前の残骸パターンが出ないことを明示的に確認する。
        Assert.DoesNotContain("erepott", mergedText, StringComparison.Ordinal);
        Assert.DoesNotContain("xlsxx", mergedText, StringComparison.Ordinal);
    }

    [Fact]
    public void M2_LowQualityLatinGarbageDoesNotOutscoreJapaneseEvenWithZeroMargin()
    {
        // M-2 の回帰テスト。ja(実測): "ファイル名:「" / en(実測): "774JIZZ:"（"774" + "JIZZ:" の2語）。
        // 修正前は validChars（生の文字数）が支配的で、候補（8文字）がベース（7文字）を上回っていた
        // （margin=2 に偶然救われていただけ）。修正後は文字ごとの妥当性で重み付けするため、
        // margin を 0 にしても日本語側が確実に勝つ。
        var ja = Line(Word("ファイル名:「", 0, 0, 80));
        var en = Line(Word("774", 10, 2, 20, 12), Word("JIZZ:", 35, 2, 35, 12));

        OcrMergeOptions zeroMargin = Options with { MarginScore = 0 };
        (IReadOnlyList<OcrLineInfo> lines, OcrMergeSummary summary) = OcrResultMerger.Merge([ja], "ja", [en], "en-US", zeroMargin);

        Assert.True(summary.Merged);
        Assert.False(summary.Decisions[0].Replaced);
        Assert.Equal("ファイル名:「", lines[0].Words[0].Text);
        Assert.True(summary.Decisions[0].BaseScore > summary.Decisions[0].CandidateScore);
    }

    [Fact]
    public void M3_JapaneseSentenceIsKeptWhenEnglishCandidateIsLowQuality()
    {
        // en 側は日本語のみの行を丸ごと捨てる（SPEC §1.6 実測）ため、この行には候補が存在しない。
        const string japaneseSentence = "画面上の文字を読み取ってクリップボードにコビーします。";
        var ja = Line(Word(japaneseSentence, 0, 0, 400));
        IReadOnlyList<OcrLineInfo> emptySecondary = [];

        (IReadOnlyList<OcrLineInfo> lines, OcrMergeSummary summary) = OcrResultMerger.Merge([ja], "ja", emptySecondary, "en-US", Options);

        Assert.True(summary.Merged);
        Assert.Equal(japaneseSentence, lines[0].Words[0].Text);
        Assert.False(summary.Decisions[0].Replaced);
        Assert.Null(summary.Decisions[0].CandidateText);
    }

    [Fact]
    public void M4_NoOverlappingCandidateKeepsBaseUnchanged()
    {
        var ja = Line(Word("テスト", 0, 0, 60));
        IReadOnlyList<OcrLineInfo> emptySecondary = [];

        (IReadOnlyList<OcrLineInfo> lines, OcrMergeSummary summary) = OcrResultMerger.Merge([ja], "ja", emptySecondary, "en-US", Options);

        Assert.True(summary.Merged);
        Assert.Equal("テスト", lines[0].Words[0].Text);
        Assert.Single(summary.Decisions);
        Assert.Null(summary.Decisions[0].CandidateText);
        Assert.False(summary.Decisions[0].Replaced);
    }

    [Fact]
    public void M5_NearTieOnCjkDominantRunKeepsBase()
    {
        // 修正 B（SPEC §1.8）: 実質同点（差が TieBreakMarginScore 未満）の場合、字種で判定する。
        // このランはベーステキストが CJK（かな）主体なのでベースを維持する。
        // （旧版はここで "qqqq" vs "qqqr" という純ラテンのフィクスチャを使っていたが、
        // それは字種判定では「ラテン主体」に分類され、修正 B の設計どおりセカンダリへ
        // 置換されるのが正しい挙動になった。純ラテン域の同点はセカンダリを採る、という
        // 新しい意図的な仕様は M8 で検証する。CJK 主体の同点でベースを守る、という
        // このテスト本来の意図は fixture を CJK に変えて維持する。）
        var jaFiller = Line(Word("これはテストの文章です", 0, 0, 200));
        var jaTarget = Line(Word("ああ", 0, 20, 40));
        var enFiller = Line(Word("wm", 0, 0, 200));
        var enTarget = Line(Word("いい", 0, 20, 40));

        (IReadOnlyList<OcrLineInfo> lines, OcrMergeSummary summary) =
            OcrResultMerger.Merge([jaFiller, jaTarget], "ja", [enFiller, enTarget], "en-US", Options);

        Assert.Equal("ja", summary.BaseLanguage);
        OcrRunDecision targetDecision = summary.Decisions[^1];
        Assert.False(targetDecision.Replaced);
        Assert.Equal("ああ", lines[1].Words[0].Text);
    }

    [Fact]
    public void M8_TieOnLatinDigitDominantRunPrefersSecondary()
    {
        // 修正 B（SPEC §1.8）の実測回帰: ja(実測)="Windows11"（1単語、スペース抜け）、
        // en(実測)="Windows" + "11"（2単語、正しくスペースあり）。どちらも重み付けスコアは同点
        // （"Windows" が辞書ヒットする分は共通、"11" の数字重みも共通のため）。
        // 従来はここでベースが勝ち "Windows11" のまま残っていた（取りこぼし）。
        // 修正後はこのランがラテン・数字主体（CJK比率0）なのでセカンダリを採用し、
        // 正しくスペースの入った "Windows 11" になる。
        var jaFiller = Line(Word("これはテストの文章です", 0, 0, 200));
        var jaTarget = Line(Word("Windows11", 0, 20, 90));
        var enFiller = Line(Word("filler", 0, 0, 200));
        var enTarget = Line(Word("Windows", 0, 20, 63), Word("11", 65, 20, 20));

        (IReadOnlyList<OcrLineInfo> lines, OcrMergeSummary summary) =
            OcrResultMerger.Merge([jaFiller, jaTarget], "ja", [enFiller, enTarget], "en-US", Options);

        Assert.Equal("ja", summary.BaseLanguage);
        OcrRunDecision targetDecision = summary.Decisions[^1];
        Assert.Equal(targetDecision.BaseScore, targetDecision.CandidateScore); // 前提: 本当に同点であること
        Assert.True(targetDecision.TieBreak);
        Assert.True(targetDecision.Replaced);
        Assert.Equal("Windows 11", string.Join(" ", lines[1].Words.Select(x => x.Text)));
    }

    [Fact]
    public void M9_TieBreakDoesNotFireOutsideTheNearTieZoneEvenOnLatinDominantRun()
    {
        // 修正 B のガード確認: ラテン主体のランでも、スコア差が TieBreakMarginScore を大きく超えていれば
        // （＝実質同点ではない）字種による置換は発動せず、通常どおり高スコア側（ここではベース）が勝つ。
        var jaFiller = Line(Word("これはテストの文章です", 0, 0, 200));
        var jaTarget = Line(Word("Windows", 0, 20, 63)); // 辞書ヒットで高スコア
        var enFiller = Line(Word("filler", 0, 0, 200));
        var enTarget = Line(Word("qz", 0, 20, 20)); // 辞書に無い低スコアの雑音

        (IReadOnlyList<OcrLineInfo> lines, OcrMergeSummary summary) =
            OcrResultMerger.Merge([jaFiller, jaTarget], "ja", [enFiller, enTarget], "en-US", Options);

        Assert.Equal("ja", summary.BaseLanguage);
        OcrRunDecision targetDecision = summary.Decisions[^1];
        Assert.False(targetDecision.TieBreak);
        Assert.False(targetDecision.Replaced);
        Assert.Equal("Windows", lines[1].Words[0].Text);
    }

    [Fact]
    public void M6_EnglishDominantContentSwitchesBaseToSecondaryLanguage()
    {
        // primary(ja エンジン)が CJK をほとんど含まない出力を返す場合、ベースは secondary(en) に切り替わる。
        var ja = Line(Word("Settings", 0, 0, 80));
        var en = Line(Word("Settings", 0, 0, 80));

        (_, OcrMergeSummary summary) = OcrResultMerger.Merge([ja], "ja", [en], "en-US", Options);

        Assert.Equal("en-US", summary.BaseLanguage);
    }

    [Fact]
    public void M7_MergedLinesStillGetCjkSpaceRemovalFromTextPostProcessor()
    {
        // en は日本語部分（"の設定"）を丸ごと捨てる（SPEC §1.6 実測）ので候補が無く、そのまま残る。
        var ja = Line(Word("Wlndows", 0, 0, 70), Word("の設定", 72, 0, 60));
        var en = Line(Word("Windows", 0, 0, 70));

        (IReadOnlyList<OcrLineInfo> lines, _) = OcrResultMerger.Merge([ja], "ja", [en], "en-US", Options);
        string processed = TextPostProcessor.Process(lines);

        Assert.Equal("Windowsの設定", processed);
    }
}
