using System.Text;

namespace ScreenOCR.Tests;

public sealed class OcrQualityScorerTests
{
    [Fact]
    public void EmbeddedJoyoKanjiTableHasExactly2136DistinctCharacters() =>
        Assert.Equal(2136, OcrQualityScorer.JoyoKanjiCount);

    [Theory]
    [InlineData("噐", true)]
    [InlineData("謬", true)]
    [InlineData("亘", true)]
    [InlineData("画", false)]
    [InlineData("面", false)]
    [InlineData("語", false)]
    [InlineData("認", false)]
    public void ImplausibleCharactersAreClassifiedFromJoyoKanji(string value, bool expected) =>
        Assert.Equal(expected, OcrQualityScorer.IsImplausible(Rune.GetRuneAt(value, 0)));

    [Fact]
    public void T13_TwoTimesMeasurementHasHighestQualityScore()
    {
        const string oneTime = """
            画面上の文字を読み取ってクリ)プ-ドに]をします。
            Windowsの設定をいて、言噐オプションを確謬してください。
            ファイル名:report_202b.xlsx(更新三202b/09/01)
            """;
        const string twoTimes = """
            画面上の文字を読み取ってクリップボードにコビーします。
            Wlndowsの設定を開いて、言語オプションを確認してください。
            ファイル名:report_2026.xlsx(更新日2026/09/01)
            """;
        const string threeTimes = """
            亘面上の文字を読み取ってクリップボードにコビーします。
            wndowsの設定を問いて、言語オプションを確認してください。
            ファイル名:「epo代-2025.x(更新日2025/09/01)
            """;
        const string fourTimes = """
            亘面上の文字を読み取ってクリップボードにコビーします。
            wtndowsの設定を問いて、言語オプションを確認してください。
            ファイル名:「epo代-2025.x(更新日2025/09/01)
            """;

        OcrQuality[] qualities = [
            OcrQualityScorer.Evaluate(oneTime),
            OcrQualityScorer.Evaluate(twoTimes),
            OcrQualityScorer.Evaluate(threeTimes),
            OcrQualityScorer.Evaluate(fourTimes)
        ];

        Assert.Equal(2, Array.IndexOf(qualities.Select(x => x.Score).ToArray(), qualities.Max(x => x.Score)) + 1);
        Assert.Equal(2, qualities[0].ImplausibleChars);
        Assert.Equal(0, qualities[1].ImplausibleChars);
        Assert.Equal(1, qualities[2].ImplausibleChars);
        Assert.Equal(1, qualities[3].ImplausibleChars);
    }

    [Fact]
    public void EmbeddedEnglishLexiconHasAtLeast500Words() => Assert.True(OcrQualityScorer.EnglishLexiconCount >= 500);

    [Fact]
    public void MixedScriptTokenIsPenalizedComparedToTheSameCharactersSplitApart()
    {
        // "e四代-2026.x" は1トークン内にラテン文字とCJK統合漢字が同居する（混在スクリプトトークン）。
        // 空白で分けて2トークンにすると混在判定を外せるので、その分だけスコアが上がるはず。
        // （生のスコア値ではなく、大小関係だけを assert する。重みを変えても方向は変わらない）
        double combined = OcrQualityScorer.Evaluate("e四代-2026.x").Score;
        double split = OcrQualityScorer.Evaluate("e 四代-2026.x").Score;
        Assert.True(split > combined);
    }

    [Fact]
    public void MixedScriptTokenIsNotFlaggedForPureLatinOrPureCjkTokens()
    {
        // "Windows"（ラテンのみ）と "画面"（CJKのみ）は混在スクリプトではないので、
        // 同じ文字数のラテンのみ・CJKのみのトークンと比較して不利な扱いを受けない
        // （＝スコアは validChars 由来分と同等になり、突出して低くならない）。
        OcrQuality windows = OcrQualityScorer.Evaluate("Windows");
        Assert.Equal(0, windows.ImplausibleChars);
        Assert.True(windows.Score > 0);

        OcrQuality screen = OcrQualityScorer.Evaluate("画面");
        Assert.Equal(0, screen.ImplausibleChars);
        Assert.True(screen.Score > 0);
    }

    [Fact]
    public void EnglishLexiconHitScoresHigherThanNonLexiconLatin()
    {
        // "Windows" は辞書ヒットで満点の文字重み(既定1.0)+トークン加点、
        // typo の "Wlndows" は辞書ヒットしないため文字重みが下がる（既定0.3）。
        Assert.True(OcrQualityScorer.Evaluate("Windows").Score > OcrQualityScorer.Evaluate("Wlndows").Score);

        // "xlsx" は辞書に無いラテン文字ランなので、基礎点は validChars 分（重み1.0相当）より
        // 低くなる（M-2 対応: 辞書非ヒットのラテン文字は既定で 0.3 倍の重みしか与えない）。
        OcrQuality xlsx = OcrQualityScorer.Evaluate("xlsx");
        Assert.True(xlsx.Score < xlsx.ValidChars);
    }

    [Fact]
    public void IsolatedLatinLetterBetweenCjkIsPenalizedComparedToATwoLetterLatinRun()
    {
        // "e四代" の "e" は CJK に挟まれた孤立ラテン1文字として減点される。
        // "ee四代" ならラン長が2になり孤立とはみなされない（混在スクリプト減点は両方に等しく効く）。
        double isolated = OcrQualityScorer.Evaluate("e四代").Score;
        double notIsolated = OcrQualityScorer.Evaluate("ee四代").Score;
        Assert.True(notIsolated > isolated);
    }

    [Fact]
    public void EvaluateFragmentCapsPenaltyAtWeightedBaseForShortFragments()
    {
        // 断片が短いと、生の減点合計が基礎点（重み付け合計）を超えることがある（例: "e四" は
        // 混在スクリプト+孤立ラテンで生の減点は 6+2=8 だが、基礎点は "e"(0.3)+"四"(implausibleなら0,
        // joyoなら1.0) 程度しかない）。EvaluateFragment は減点を基礎点でクランプするため、
        // スコアが極端な負値にならない。
        OcrQuality fragment = OcrQualityScorer.EvaluateFragment("e四");
        Assert.True(fragment.Score >= 0);
    }

    [Fact]
    public void CustomWeightsAreHonoredInsteadOfHardcodedDefaults()
    {
        var zeroWeights = new OcrScoreWeights(0, 0, 0, 0, 0, 0, 0, 0);
        OcrQuality withZeroWeights = OcrQualityScorer.Evaluate("e四代-2026.x", zeroWeights);
        Assert.Equal(0, withZeroWeights.Score);
    }
}
