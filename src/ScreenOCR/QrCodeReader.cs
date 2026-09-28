using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ZXing;
using ZXing.Common;

namespace ScreenOCR;

/// <summary>
/// デコードできた QR コード 1 個ぶん。<see cref="Bounds"/> は元画像（等倍）の座標系で、
/// ZXing が返す位置決めパターンの中心から作るため、シンボル外周より一回り内側になる。
/// </summary>
public sealed record QrCodeMatch(string Text, RectF Bounds);

/// <summary>QR 走査の結果。<see cref="Matches"/> が空なら QR は見つからなかった。</summary>
public sealed record QrScanResult(IReadOnlyList<QrCodeMatch> Matches, string Attempt, long ElapsedMilliseconds)
{
    public bool Found => Matches.Count > 0;

    /// <summary>クリップボードへ入れる文字列。複数見つかった場合は検出順に改行で連結する。</summary>
    public string Text => string.Join(Environment.NewLine, Matches.Select(x => x.Text));

    public static QrScanResult NotFound(long elapsedMilliseconds) => new([], string.Empty, elapsedMilliseconds);
}

/// <summary>
/// 選択範囲の画像から QR コードを読み取る。デコードは ZXing.Net（ローカル完結）で行い、
/// 画像も結果も外部へ送信しない。OCR と違い、成否は「読めた／読めなかった」の二値なので
/// スコアリングはせず、最初に成功した試行の結果をそのまま採用する。
/// </summary>
public static class QrCodeReader
{
    /// <summary>試行名: 等倍。</summary>
    private const string PlainAttemptName = "QR";

    /// <summary>試行名: 白黒反転（ダークテーマの画面に描かれた反転 QR 用）。</summary>
    private const string InvertedAttemptName = "QR-inv";

    /// <summary>試行名: 2 倍拡大（モジュールが 1〜2 px しかない小さな QR 用）。</summary>
    private const string UpscaledAttemptName = "QR-2x";

    /// <summary>2 倍拡大の再試行を行う上限（長辺 px）。広い選択範囲で無駄な再走査をしない。</summary>
    private const int UpscaleLongEdgeLimit = 700;

    private static int _encodingsRegistered;

    /// <summary>
    /// 等倍 → 反転 → 2 倍拡大の順に試し、最初に読めた試行の結果を返す。すべて空なら
    /// <see cref="QrScanResult.Found"/> が <c>false</c> の結果を返す（例外は投げない）。
    /// </summary>
    public static QrScanResult Scan(Bitmap source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureEncodingsRegistered();
        var timer = Stopwatch.StartNew();
        BarcodeReaderGeneric reader = CreateReader();

        IReadOnlyList<QrCodeMatch>? matches = RunAttempt(reader, source, 1.0, false, cancellationToken);
        if (matches is not null) return new(matches, PlainAttemptName, timer.ElapsedMilliseconds);

        matches = RunAttempt(reader, source, 1.0, true, cancellationToken);
        if (matches is not null) return new(matches, InvertedAttemptName, timer.ElapsedMilliseconds);

        if (Math.Max(source.Width, source.Height) < UpscaleLongEdgeLimit)
        {
            matches = RunAttempt(reader, source, 2.0, false, cancellationToken);
            if (matches is not null) return new(matches, UpscaledAttemptName, timer.ElapsedMilliseconds);
        }

        return QrScanResult.NotFound(timer.ElapsedMilliseconds);
    }

    /// <summary>
    /// デコード結果がブラウザーで開ける URL かどうか。QR の内容を自動で開くことはせず、
    /// 通知のクリックで開く導線を出すかどうかの判定にだけ使う。
    /// </summary>
    public static bool TryGetHttpUrl(string text, [NotNullWhen(true)] out Uri? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        url = parsed;
        return true;
    }

    /// <summary>
    /// 読む順（上から下、同じ行では左から右）に並べ替える。ZXing の検出順は画面上の位置と
    /// 関係がないため、そのままではクリップボードの並びも「1 つ目」も見た目と食い違う。
    /// 上端のずれが最も低い QR の高さの半分までなら同じ行とみなす（固定の帯で切ると、
    /// 境目をまたいだだけの横並びが別の行に分かれてしまうため）。
    /// </summary>
    public static IReadOnlyList<QrCodeMatch> InReadingOrder(IReadOnlyList<QrCodeMatch> matches)
    {
        if (matches.Count < 2) return matches;
        float tolerance = Math.Max(1f, matches.Min(x => x.Bounds.Height) / 2f);
        var remaining = matches.OrderBy(x => x.Bounds.Top).ToList();
        var ordered = new List<QrCodeMatch>(matches.Count);
        while (remaining.Count > 0)
        {
            float rowTop = remaining[0].Bounds.Top;
            List<QrCodeMatch> row = remaining.Where(x => x.Bounds.Top - rowTop <= tolerance).ToList();
            remaining.RemoveAll(row.Contains);
            ordered.AddRange(row.OrderBy(x => x.Bounds.Left));
        }
        return ordered;
    }

    /// <summary>
    /// 読む順で最初に見つかった <c>http</c> / <c>https</c> のリンク。1 回の選択に複数の QR が
    /// あってもリンクは 1 つだけ開くため、どれを開くかをここで決める。無ければ <c>null</c>。
    /// </summary>
    public static Uri? FindFirstHttpUrl(IEnumerable<QrCodeMatch> matches)
    {
        foreach (QrCodeMatch match in matches)
            if (TryGetHttpUrl(match.Text, out Uri? url)) return url;
        return null;
    }

    /// <summary>
    /// 開く前に見せる URL の文字列。<see cref="Uri.AbsoluteUri"/> をそのまま出さず、次を守って組み立てる。
    /// <list type="bullet">
    /// <item>ホストは punycode（<see cref="Uri.IdnHost"/>）。同形異字を見分けられるようにするため。</item>
    /// <item>ユーザー情報と既定でないポートも省かない。<c>https://accounts.example.com@evil.example/</c> の
    /// ように、ホストらしく見せかけた部分を隠さないため。</item>
    /// <item>双方向制御文字（<c>U+202E</c> など）を取り除く。表示順を反転させて拡張子や経路を
    /// 偽装できるため。</item>
    /// </list>
    /// 実際に開くのはこの文字列ではなく <see cref="Uri.AbsoluteUri"/>。
    /// </summary>
    public static string DescribeUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var builder = new StringBuilder(url.Scheme).Append("://");
        if (!string.IsNullOrEmpty(url.UserInfo)) builder.Append(url.UserInfo).Append('@');
        builder.Append(url.IdnHost);
        if (!url.IsDefaultPort) builder.Append(':').Append(url.Port);
        builder.Append(url.PathAndQuery).Append(url.Fragment);

        var display = new StringBuilder(builder.Length);
        foreach (char character in builder.ToString())
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.Format or UnicodeCategory.Control) continue;
            display.Append(character);
        }
        return display.ToString();
    }

    private static IReadOnlyList<QrCodeMatch>? RunAttempt(
        BarcodeReaderGeneric reader, Bitmap source, double scale, bool invert, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Bitmap? resized = scale == 1.0 ? null : ImagePreprocessor.Resize(source, scale);
        try
        {
            LuminanceSource luminance = ToLuminanceSource(resized ?? source);
            if (invert) luminance = luminance.invert();
            Result[]? results = reader.DecodeMultiple(luminance);
            if (results is null) return null;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var matches = new List<QrCodeMatch>();
            foreach (Result result in results)
            {
                // DecodeMultiple は回転を変えた再走査で同じシンボルを 2 回返すことがあるため、
                // 同じ文字列は 1 件にまとめる。
                if (string.IsNullOrEmpty(result?.Text) || !seen.Add(result.Text)) continue;
                matches.Add(new QrCodeMatch(result.Text, ToBounds(result.ResultPoints, scale)));
            }
            return matches.Count == 0 ? null : InReadingOrder(matches);
        }
        finally { resized?.Dispose(); }
    }

    private static BarcodeReaderGeneric CreateReader() => new()
    {
        AutoRotate = true,
        Options = new DecodingOptions
        {
            PossibleFormats = [BarcodeFormat.QR_CODE],
            TryHarder = true,
            PureBarcode = false
        }
    };

    private static RectF ToBounds(ResultPoint[]? points, double scale)
    {
        if (points is null || points.Length == 0) return default;
        float left = points.Min(x => x.X), right = points.Max(x => x.X);
        float top = points.Min(x => x.Y), bottom = points.Max(x => x.Y);
        return new RectF(
            (float)(left / scale), (float)(top / scale),
            (float)((right - left) / scale), (float)((bottom - top) / scale));
    }

    private static RGBLuminanceSource ToLuminanceSource(Bitmap source)
    {
        using var converted = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        Rectangle rectangle = new(0, 0, converted.Width, converted.Height);
        // 画素を 1:1 で写す。DrawImageUnscaled は名前に反して、画像とキャンバスの解像度が違うと
        // その比だけ拡大縮小する。ImagePreprocessor.Resize の出力は 96 dpi、new Bitmap(w,h,fmt) は
        // 画面 DPI（125% なら 120）になるため、拡大した試行だけが 1.25 倍されて右下が欠けていた。
        using (Graphics graphics = Graphics.FromImage(converted))
            graphics.DrawImage(source, rectangle, rectangle, GraphicsUnit.Pixel);
        BitmapData data = converted.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowSize = converted.Width * 4;
            byte[] pixels = new byte[rowSize * converted.Height];
            for (int y = 0; y < converted.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * rowSize, rowSize);
            // BGR32 はアルファを読まない。BitBlt 由来のビットマップはアルファが 0 のことがあるため、
            // アルファを見る BGRA32 は使わない。
            return new RGBLuminanceSource(pixels, converted.Width, converted.Height, RGBLuminanceSource.BitmapFormat.BGR32);
        }
        finally { converted.UnlockBits(data); }
    }

    /// <summary>
    /// Kanji モード（Shift_JIS）や ECI 指定の QR をデコードするには CodePages のエンコーディングが要る。
    /// .NET では既定で登録されていないため、最初の走査前に一度だけ登録する。
    /// </summary>
    private static void EnsureEncodingsRegistered()
    {
        if (Interlocked.Exchange(ref _encodingsRegistered, 1) == 1) return;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}
