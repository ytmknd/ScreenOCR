using System.Drawing;

namespace ScreenOCR.Tests;

public sealed class ToastTests
{
    private static readonly Size ToastSize = new(390, 94);

    [Theory]
    [InlineData(false, false, 1200)]
    [InlineData(true, false, 4000)]
    [InlineData(false, true, 8000)]
    [InlineData(true, true, 8000)]
    public void ActionableToastsStayLongEnoughToAimAt(bool error, bool actionable, int expected) =>
        Assert.Equal(expected, Toast.CalculateDurationMs(error, actionable));

    [Fact]
    public void ActionableToastAppearsJustOffTheCursor()
    {
        Point location = Toast.CalculateCursorLocation(new Point(500, 500), new Rectangle(0, 0, 1920, 1080), ToastSize);

        Assert.Equal(new Point(512, 512), location);
        // 出た瞬間にホバー扱いにならないよう、カーソルはトーストの外に残す。
        Assert.False(new Rectangle(location, ToastSize).Contains(new Point(500, 500)));
    }

    [Theory]
    [InlineData(1900, 500, 1498, 512)]
    [InlineData(500, 1070, 512, 964)]
    [InlineData(1900, 1070, 1498, 964)]
    public void ToastFlipsToTheOtherSideOfTheCursorAtScreenEdges(int cursorX, int cursorY, int expectedX, int expectedY) =>
        Assert.Equal(
            new Point(expectedX, expectedY),
            Toast.CalculateCursorLocation(new Point(cursorX, cursorY), new Rectangle(0, 0, 1920, 1080), ToastSize));

    [Fact]
    public void ToastStaysOnTheMonitorLeftOfTheOrigin()
    {
        Point location = Toast.CalculateCursorLocation(new Point(-100, 500), new Rectangle(-1920, 0, 1920, 1080), ToastSize);

        Assert.Equal(new Point(-502, 512), location);
    }

    /// <summary>テスト用の等幅メジャラー。1 文字 = 1 単位。</summary>
    private static int Monospace(string text) => text.Length;

    [Fact]
    public void TextShorterThanTheWidthIsLeftAlone()
    {
        WrappedText wrapped = Toast.WrapToWidth("abcdefgh", 10, 6, Monospace);

        Assert.Equal(["abcdefgh"], wrapped.Lines);
        Assert.False(wrapped.Truncated);
    }

    /// <summary>URL には空白が無いので、単語ではなく文字単位で折り返せること。</summary>
    [Fact]
    public void TextWithoutSpacesIsWrappedByCharacter()
    {
        WrappedText wrapped = Toast.WrapToWidth("https://example.com/a/b/c", 10, 6, Monospace);

        Assert.Equal(["https://ex", "ample.com/", "a/b/c"], wrapped.Lines);
        Assert.False(wrapped.Truncated);
    }

    [Fact]
    public void ExistingLineBreaksStartANewLine()
    {
        WrappedText wrapped = Toast.WrapToWidth("one\r\ntwo", 10, 6, Monospace);

        Assert.Equal(["one", "two"], wrapped.Lines);
        Assert.False(wrapped.Truncated);
    }

    /// <summary>
    /// 省略は末尾だけ。URL は先頭にスキームとホストが来るので、これで行き先は隠せない。
    /// </summary>
    [Fact]
    public void OverlongTextLosesOnlyItsTailAndSaysSo()
    {
        WrappedText wrapped = Toast.WrapToWidth("https://example.com/verylongpath/that/keeps/going", 10, 2, Monospace);

        Assert.True(wrapped.Truncated);
        Assert.Equal(2, wrapped.Lines.Count);
        Assert.Equal("https://ex", wrapped.Lines[0]);
        Assert.EndsWith("…", wrapped.Lines[1]);
        // 省略記号を足しても幅をはみ出さないこと。
        Assert.All(wrapped.Lines, line => Assert.True(Monospace(line) <= 10));
        // 先頭（スキームとホスト）はそのまま残ること。
        Assert.StartsWith("https://example.com", string.Concat(wrapped.Lines).TrimEnd('…'));
    }

    [Fact]
    public void TextThatExactlyFillsTheLastLineIsNotMarkedTruncated()
    {
        WrappedText wrapped = Toast.WrapToWidth("abcdefghij", 10, 1, Monospace);

        Assert.Equal(["abcdefghij"], wrapped.Lines);
        Assert.False(wrapped.Truncated);
    }

    [Fact]
    public void RemainingParagraphsBeyondTheLineBudgetAreElided()
    {
        WrappedText wrapped = Toast.WrapToWidth("one\ntwo\nthree", 10, 2, Monospace);

        Assert.True(wrapped.Truncated);
        Assert.Equal(["one", "two…"], wrapped.Lines);
    }

    /// <summary>作業領域よりトーストが大きい場合でも、左上へ寄せて座標が外へ飛ばないこと。</summary>
    [Fact]
    public void ToastIsClampedWhenItDoesNotFitTheWorkingArea() =>
        Assert.Equal(
            new Point(0, 0),
            Toast.CalculateCursorLocation(new Point(10, 10), new Rectangle(0, 0, 300, 60), ToastSize));
}
