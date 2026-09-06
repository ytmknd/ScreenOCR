namespace ScreenOCR.Tests;

public sealed class HotKeyParserTests
{
    [Theory]
    [InlineData("Ctrl+Alt+6", HotKeyModifiers.Control | HotKeyModifiers.Alt, 0x36)]
    [InlineData("Ctrl+Shift+F9", HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x78)]
    [InlineData("Win+Alt+A", HotKeyModifiers.Win | HotKeyModifiers.Alt, 0x41)]
    public void ValidGesturesParse(string text, HotKeyModifiers expectedModifiers, uint expectedKey)
    {
        Assert.True(HotKeyParser.TryParse(text, out HotKeyGesture gesture));
        Assert.Equal(expectedModifiers | HotKeyModifiers.NoRepeat, gesture.Modifiers);
        Assert.Equal(expectedKey, gesture.VirtualKey);
    }

    [Theory]
    [InlineData("Ctrl+")]
    [InlineData("")]
    [InlineData("Foo+1")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("A")]
    public void InvalidGesturesAreRejected(string text) => Assert.False(HotKeyParser.TryParse(text, out _));
}
