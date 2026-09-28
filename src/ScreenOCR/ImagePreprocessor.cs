using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ScreenOCR;

public static class ImagePreprocessor
{
    public static IReadOnlyList<double> GetCandidateScales(
        int width,
        int height,
        double maxScale,
        int maxDimension,
        float dpiPercent = 100f)
    {
        int longEdge = Math.Max(width, height);
        if (longEdge <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (maxScale <= 0) throw new ArgumentOutOfRangeException(nameof(maxScale));
        if (maxDimension <= 0) throw new ArgumentOutOfRangeException(nameof(maxDimension));

        double[] initial = longEdge < 1400 ? [2.0, 3.0, 4.0] : [1.0, 2.0];
        IEnumerable<double> adjusted = dpiPercent > 150f
            ? initial.Select(scale => Math.Max(1.0, scale - 1.0))
            : initial;

        return adjusted
            .Select(scale => Math.Min(scale, maxScale))
            .Distinct()
            .Where(scale => longEdge * scale <= maxDimension)
            .ToArray();
    }

    public static Bitmap Resize(Bitmap source, double scale)
    {
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        result.SetResolution(96, 96);
        using Graphics graphics = Graphics.FromImage(result);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return result;
    }

    public static Bitmap Invert(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(result);
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix(new[]
        {
            new[] { -1f, 0, 0, 0, 0 }, new[] { 0, -1f, 0, 0, 0 },
            new[] { 0, 0, -1f, 0, 0 }, new[] { 0, 0, 0, 1f, 0 },
            new[] { 1f, 1f, 1f, 0, 1f }
        }));
        graphics.DrawImage(source, new Rectangle(0, 0, result.Width, result.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        return result;
    }

    public static Bitmap RotateClockwise(Bitmap source)
    {
        var result = new Bitmap(source);
        result.RotateFlip(RotateFlipType.Rotate90FlipNone);
        return result;
    }

    public static double CalculateAverageLuminance(Bitmap bitmap)
    {
        using var converted = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
        Rectangle rectangle = new(0, 0, converted.Width, converted.Height);
        // 画素を 1:1 で写す（DrawImageUnscaled は解像度差ぶん拡大縮小してしまう）。
        using (Graphics graphics = Graphics.FromImage(converted))
            graphics.DrawImage(bitmap, rectangle, rectangle, GraphicsUnit.Pixel);
        BitmapData data = converted.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int step = Math.Max(1, (int)Math.Sqrt((double)(converted.Width * converted.Height) / 100_000));
            byte[] row = new byte[converted.Width * 4];
            double total = 0;
            long count = 0;
            for (int y = 0; y < converted.Height; y += step)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < converted.Width; x += step)
                {
                    int offset = x * 4;
                    total += 0.2126 * row[offset + 2] + 0.7152 * row[offset + 1] + 0.0722 * row[offset];
                    count++;
                }
            }
            return total / (count * 255.0);
        }
        finally { converted.UnlockBits(data); }
    }
}
