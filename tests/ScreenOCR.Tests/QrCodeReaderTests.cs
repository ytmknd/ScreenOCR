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

    /// <summary>
    /// 画像の解像度がキャンバスと違っても、画素を 1:1 で読むこと。DrawImageUnscaled は名前に反して
    /// 解像度差ぶん拡大縮小するため、以前は拡大した試行（96 dpi）が画面 DPI のキャンバスに載ると
    /// 1.25 倍されて右下が欠け、125% の環境で小さな QR が読めなくなっていた。
    /// </summary>
    [Theory]
    [InlineData(48f)]
    [InlineData(72f)]
    [InlineData(120f)]
    [InlineData(144f)]
    public void ResolutionOfTheSourceBitmapDoesNotScaleThePixels(float dpi)
    {
        using Bitmap bitmap = LoadFixture("qr-url.png");
        bitmap.SetResolution(dpi, dpi);

        Assert.Equal("https://github.com/ytmknd/ScreenOCR", QrCodeReader.Scan(bitmap).Text);
    }

    [Fact]
    public void ReadsSmallQrCode()
    {
        using Bitmap bitmap = LoadFixture("qr-small.png");

        QrScanResult result = QrCodeReader.Scan(bitmap);

        Assert.Equal("https://github.com/ytmknd/ScreenOCR", result.Text);
    }

    /// <summary>qr-multi.png は左が URL、右が日本語。読む順に並ぶこと。</summary>
    [Fact]
    public void ReadsEveryCodeInSelectionInReadingOrder()
    {
        using Bitmap bitmap = LoadFixture("qr-multi.png");

        QrScanResult result = QrCodeReader.Scan(bitmap);

        Assert.Equal(
            ["https://github.com/ytmknd/ScreenOCR", "画面の QR コードを読み取ります。"],
            result.Matches.Select(x => x.Text));
        Assert.Equal(2, result.Text.Split(Environment.NewLine).Length);
    }

    [Fact]
    public void ReadingOrderGoesTopToBottomThenLeftToRight()
    {
        QrCodeMatch below = new("below", new RectF(10, 200, 100, 100));
        QrCodeMatch right = new("right", new RectF(300, 10, 100, 100));
        QrCodeMatch left = new("left", new RectF(10, 12, 100, 100));

        Assert.Equal(
            ["left", "right", "below"],
            QrCodeReader.InReadingOrder([below, right, left]).Select(x => x.Text));
    }

    /// <summary>横並びの 2 つが固定の帯の境目をまたいでも、別の行に分かれないこと。</summary>
    [Fact]
    public void SideBySideCodesStayOnTheSameRowAcrossABandBoundary()
    {
        QrCodeMatch right = new("right", new RectF(300, 101, 100, 100));
        QrCodeMatch left = new("left", new RectF(10, 99, 100, 100));

        Assert.Equal(["left", "right"], QrCodeReader.InReadingOrder([right, left]).Select(x => x.Text));
    }

    [Fact]
    public void FindsTheFirstLinkInReadingOrderSkippingCodesWithout()
    {
        QrCodeMatch text = new("ただのテキスト", new RectF(0, 0, 10, 10));
        QrCodeMatch first = new("https://example.com/one", new RectF(0, 0, 10, 10));
        QrCodeMatch second = new("https://example.com/two", new RectF(0, 0, 10, 10));

        Assert.Equal("https://example.com/one", QrCodeReader.FindFirstHttpUrl([text, first, second])?.AbsoluteUri);
        Assert.Null(QrCodeReader.FindFirstHttpUrl([text]));
        Assert.Null(QrCodeReader.FindFirstHttpUrl([]));
    }

    /// <summary>
    /// 開く直前に見せる文字列。ホストは punycode（同形異字を見分けるため）、経路・クエリ・
    /// 素片は省略しない（同じホスト下の転送や偽ログインを見分けるため）。
    /// </summary>
    [Theory]
    // 同形異字: キリル文字の "а" を混ぜた "аpple.com"
    [InlineData("https://аpple.com/x", "https://xn--pple-43d.com/x")]
    // 経路・クエリ・素片を落とさない
    [InlineData("https://example.com/login?next=https%3A%2F%2Fevil.test%2F#top",
                "https://example.com/login?next=https%3A%2F%2Fevil.test%2F#top")]
    // ホストらしく見せかけたユーザー情報を隠さない
    [InlineData("https://accounts.example.com@evil.test/go", "https://accounts.example.com@evil.test/go")]
    // 既定でないポートは出す。既定のポートは出さない
    [InlineData("https://example.com:8443/a", "https://example.com:8443/a")]
    [InlineData("https://example.com:443/a", "https://example.com/a")]
    // 表示順を反転させる双方向制御文字（U+202E）は Uri が先に %E2%80%AE へ逃がす。
    // 見える形になっていれば偽装には使えないので、この形のまま出す。
    [InlineData("https://example.com/‮gnp.exe", "https://example.com/%E2%80%AEgnp.exe")]
    [InlineData("https://example.com/", "https://example.com/")]
    public void UrlShownBeforeOpeningKeepsEverythingThatIdentifiesTheDestination(string text, string expected)
    {
        Assert.True(QrCodeReader.TryGetHttpUrl(text, out Uri? url));
        Assert.Equal(expected, QrCodeReader.DescribeUrl(url));
    }

    /// <summary>
    /// 表示文字列に不可視文字を残さないこと。Uri が逃がしきれなかったぶんの保険で、
    /// ここが崩れると見た目と行き先がずれる。
    /// </summary>
    [Theory]
    [InlineData("https://example.com/‮dnp.exe")]
    [InlineData("https://example.com/​‎a")]
    [InlineData("https://user‮@example.com/a")]
    public void DisplayStringNeverContainsInvisibleCharacters(string text)
    {
        Assert.True(QrCodeReader.TryGetHttpUrl(text, out Uri? url));

        string display = QrCodeReader.DescribeUrl(url);

        Assert.DoesNotContain(display, character =>
        {
            System.Globalization.UnicodeCategory category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character);
            return category is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Control;
        });
    }

    /// <summary>表示は punycode に直すが、実際に開くのは元の URL のままであること。</summary>
    [Fact]
    public void TheUrlThatIsOpenedIsNotTheDisplayString()
    {
        Assert.True(QrCodeReader.TryGetHttpUrl("https://аpple.com/x", out Uri? url));

        Assert.Equal("https://xn--pple-43d.com/x", QrCodeReader.DescribeUrl(url));
        Assert.Equal("https://аpple.com/x", url.AbsoluteUri);
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
