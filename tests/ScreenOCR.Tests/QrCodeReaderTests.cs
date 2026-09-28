using System.Drawing;

namespace ScreenOCR.Tests;

/// <summary>
/// 固定画像（<c>tests/fixtures/qr-*.png</c>、OpenCV の QR エンコーダーで生成）を
/// <see cref="QrCodeReader"/> で読み直す。生成側とデコード側が別実装なので、
/// ZXing の往復だけを見る自己完結テストにはならない。
/// </summary>
public sealed class QrCodeReaderTests
{
    private static Bitmap LoadFixture(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        using var loaded = new Bitmap(path);
        return new Bitmap(loaded);
    }

    [Fact]
    public void ReadsAsciiUrlFromFixture()
    {
        using Bitmap bitmap = LoadFixture("qr-url.png");

        QrScanResult result = QrCodeReader.Scan(bitmap);

        Assert.True(result.Found);
        Assert.Equal("https://github.com/ytmknd/ScreenOCR", result.Text);
        Assert.Equal("QR", result.Attempt);
    }

    [Fact]
    public void ReadsJapaneseByteModeFixture()
    {
        using Bitmap bitmap = LoadFixture("qr-japanese-utf8.png");

        QrScanResult result = QrCodeReader.Scan(bitmap);

        Assert.Equal("画面の QR コードを読み取ります。", result.Text);
    }

    /// <summary>Kanji モード（Shift_JIS）は CodePages のエンコーディング登録が無いと読めない。</summary>
    [Fact]
    public void ReadsKanjiModeShiftJisFixture()
    {
        using Bitmap bitmap = LoadFixture("qr-kanji-sjis.png");

        QrScanResult result = QrCodeReader.Scan(bitmap);

        Assert.Equal("日本語のテスト", result.Text);
    }

    [Fact]
    public void ReadsInvertedQrCodeWithSecondAttempt()
    {
        using Bitmap original = LoadFixture("qr-url.png");
        using Bitmap inverted = ImagePreprocessor.Invert(original);

        QrScanResult result = QrCodeReader.Scan(inverted);

        Assert.Equal("https://github.com/ytmknd/ScreenOCR", result.Text);
        Assert.Equal("QR-inv", result.Attempt);
    }

    [Fact]
    public void ReadsSmallQrCode()
    {
        using Bitmap bitmap = LoadFixture("qr-small.png");

        QrScanResult result = QrCodeReader.Scan(bitmap);

        Assert.Equal("https://github.com/ytmknd/ScreenOCR", result.Text);
    }

    [Fact]
    public void ReadsEveryCodeInSelectionSeparatedByNewLine()
    {
        using Bitmap bitmap = LoadFixture("qr-multi.png");

        QrScanResult result = QrCodeReader.Scan(bitmap);

        Assert.Equal(2, result.Matches.Count);
        Assert.Contains("https://github.com/ytmknd/ScreenOCR", result.Matches.Select(x => x.Text));
        Assert.Contains("画面の QR コードを読み取ります。", result.Matches.Select(x => x.Text));
        Assert.Equal(2, result.Text.Split(Environment.NewLine).Length);
    }

    [Fact]
    public void BoundsStayInsideTheSourceImage()
    {
        using Bitmap bitmap = LoadFixture("qr-url.png");

        QrCodeMatch match = Assert.Single(QrCodeReader.Scan(bitmap).Matches);

        Assert.InRange(match.Bounds.Left, 0, bitmap.Width);
        Assert.InRange(match.Bounds.Top, 0, bitmap.Height);
        Assert.InRange(match.Bounds.Right, match.Bounds.Left, bitmap.Width);
        Assert.InRange(match.Bounds.Bottom, match.Bounds.Top, bitmap.Height);
        Assert.True(match.Bounds.Width > 0 && match.Bounds.Height > 0);
    }

    /// <summary>QR の無い文字画像では、例外にも誤検出にもならず「見つからなかった」を返す。</summary>
    [Fact]
    public void ReturnsNotFoundForTextOnlyImage()
    {
        using Bitmap bitmap = LoadFixture("japanese-9pt.png");

        QrScanResult result = QrCodeReader.Scan(bitmap);

        Assert.False(result.Found);
        Assert.Empty(result.Matches);
        Assert.Equal(string.Empty, result.Text);
    }

    [Theory]
    [InlineData("https://example.com/a?b=1", true)]
    [InlineData("http://example.com", true)]
    [InlineData("HTTPS://EXAMPLE.COM", true)]
    [InlineData("example.com", false)]
    [InlineData("ただのテキスト", false)]
    [InlineData("mailto:someone@example.com", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    public void OnlyHttpAndHttpsPayloadsOfferTheOpenAction(string text, bool expected)
    {
        Assert.Equal(expected, QrCodeReader.TryGetHttpUrl(text, out Uri? url));
        Assert.Equal(expected, url is not null);
    }
}
