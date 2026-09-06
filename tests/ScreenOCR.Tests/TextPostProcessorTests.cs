namespace ScreenOCR.Tests;

public sealed class TextPostProcessorTests
{
    [Fact] public void T1_JapaneseWordsHaveNoSpaces() => Assert.Equal("日本語のテキスト", Process(Line("日本", "語", "の", "テキスト")));
    [Fact] public void T2_LatinWordsWithLargeGapHaveSpace()
    {
        var line = new OcrLineInfo([Word("Hello", 0, 20), Word("world", 30, 20)]);
        Assert.Equal("Hello world", Process(line));
    }
    [Fact] public void T3_JapaneseCommandHasNoSpaces() => Assert.Equal("設定ファイルを開く", Process(Line("設定", "ファイル", "を", "開く")));
    [Fact] public void T4_LatinNextToCjkHasNoSpace() => Assert.Equal("Windowsの設定", Process(Line("Windows", "の", "設定")));
    [Fact] public void T5_LatinSmallGapsHaveNoSpaces()
    {
        var line = new OcrLineInfo([Word("ID", 0, 20), Word(":", 21, 10), Word("12345", 32, 50)]);
        Assert.Equal("ID:12345", Process(line));
    }
    [Fact] public void T6_SeparateLinesRemainCrlf() => Assert.Equal("これは\r\nテストです", Process(LineAt("これは", 0), LineAt("テストです", 20)));
    [Fact] public void T7_JoinLinesJoinsCjk()
    {
        Assert.Equal("これはテストです", Process(new TextProcessingOptions { JoinLines = true }, LineAt("これは", 0), LineAt("テストです", 20)));
    }
    [Fact] public void T8_SentenceEndPreventsJoin()
    {
        Assert.Equal("これは。\r\nテストです", Process(new TextProcessingOptions { JoinLines = true }, LineAt("これは。", 0), LineAt("テストです", 20)));
    }
    [Fact] public void T9_DisabledCjkRemovalAlwaysAddsSpaces()
    {
        Assert.Equal("日本 語 の テキスト", Process(new TextProcessingOptions { RemoveCjkSpaces = false }, Line("日本", "語", "の", "テキスト")));
    }
    [Fact] public void T10_LinesAreSortedByTop()
    {
        Assert.Equal("上\r\n下", Process(LineAt("下", 40), LineAt("上", 10)));
    }
    [Fact] public void T11_InvalidRegexIsIgnored()
    {
        var bad = new ReplacementRule { Pattern = "[", Replacement = "x", Regex = true };
        var good = new ReplacementRule { Pattern = "誤", Replacement = "正", Regex = false };
        string result = Process(new TextProcessingOptions { Replacements = [bad, good] }, Line("誤", "認識"));
        Assert.False(bad.IsValid);
        Assert.Equal("正認識", result);
    }
    [Fact] public void T12_AllNewlinesAreCrlf()
    {
        string result = Process(LineAt("一", 0), LineAt("二", 20), LineAt("三", 40));
        Assert.Contains("\r\n", result);
        Assert.DoesNotContain("\n", result.Replace("\r\n", string.Empty, StringComparison.Ordinal));
    }

    [Fact] public void JoinLines_AddsSpaceForLatinLines()
    {
        Assert.Equal("Hello world", Process(new TextProcessingOptions { JoinLines = true }, LineAt("Hello", 0), LineAt("world", 20)));
    }
    [Fact] public void FullWidthNormalizationDoesNotUseCompatibilityDecomposition()
    {
        Assert.Equal("ABC ①", Process(new TextProcessingOptions { NormalizeFullWidth = true }, Line("ＡＢＣ", "　", "①")));
    }
    [Fact] public void HalfWidthKanaConversionDoesNotChangeOtherCompatibilityCharacters()
    {
        Assert.Equal("ガ①Ａ", Process(new TextProcessingOptions { HalfToFullKana = true }, Line("ｶﾞ①Ａ")));
    }

    private static string Process(params OcrLineInfo[] lines) => TextPostProcessor.Process(lines);
    private static string Process(TextProcessingOptions options, params OcrLineInfo[] lines) => TextPostProcessor.Process(lines, options);
    private static OcrLineInfo Line(params string[] words)
    {
        float x = 0;
        var output = new List<OcrWordInfo>();
        foreach (string text in words)
        {
            float width = Math.Max(10, text.Length * 10);
            output.Add(Word(text, x, width));
            x += width + 2;
        }
        return new OcrLineInfo(output);
    }
    private static OcrLineInfo LineAt(string text, float top) => new([new(text, new RectF(0, top, text.Length * 10, 12))]);
    private static OcrWordInfo Word(string text, float left, float width) => new(text, new RectF(left, 0, width, 12));
}
