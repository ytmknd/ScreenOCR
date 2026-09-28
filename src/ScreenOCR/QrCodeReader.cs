using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Imaging;
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
            return matches.Count == 0 ? null : matches;
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
        using (Graphics graphics = Graphics.FromImage(converted)) graphics.DrawImageUnscaled(source, 0, 0);
        Rectangle rectangle = new(0, 0, converted.Width, converted.Height);
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
