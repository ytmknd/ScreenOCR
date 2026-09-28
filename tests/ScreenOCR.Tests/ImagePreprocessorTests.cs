using System.Drawing;

namespace ScreenOCR.Tests;

public sealed class ImagePreprocessorTests
{
    [Theory]
    [InlineData(620, 90, new double[] { 2.0, 3.0, 4.0 })]
    [InlineData(1399, 100, new double[] { 2.0, 3.0, 4.0 })]
    [InlineData(1400, 100, new double[] { 1.0, 2.0 })]
    [InlineData(2000, 100, new double[] { 1.0, 2.0 })]
    public void CandidateScalesFollowLongEdge(int width, int height, double[] expected) =>
        Assert.Equal(expected, ImagePreprocessor.GetCandidateScales(width, height, 4, 10000));

    [Fact]
    public void CandidateOverRuntimeMaxDimensionIsRemoved() =>
        Assert.Equal([1.0], ImagePreprocessor.GetCandidateScales(3000, 100, 4, 5000));

    [Fact]
    public void ConfiguredMaximumCapsAndDeduplicatesCandidates() =>
        Assert.Equal([2.0, 2.5], ImagePreprocessor.GetCandidateScales(620, 90, 2.5, 10000));

    [Fact]
    public void HighDpiLowersEveryCandidateOneStep()
    {
        Assert.Equal([1.0, 2.0, 3.0], ImagePreprocessor.GetCandidateScales(620, 90, 4, 10000, 151));
        Assert.Equal([2.0, 3.0, 4.0], ImagePreprocessor.GetCandidateScales(620, 90, 4, 10000, 150));
    }

    /// <summary>
    /// 解像度の違う画像を渡しても、画素を 1:1 で読むこと。以前の DrawImageUnscaled は解像度差ぶん
    /// 拡大縮小するため、48 dpi の画像は 2 倍に引き伸ばされて右半分（黒）が視野から外れていた。
    /// </summary>
    [Theory]
    [InlineData(48f)]
    [InlineData(96f)]
    [InlineData(120f)]
    public void AverageLuminanceIgnoresTheBitmapResolution(float dpi)
    {
        using var bitmap = new Bitmap(100, 20);
        bitmap.SetResolution(dpi, dpi);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.Black, new Rectangle(50, 0, 50, 20));
        }

        Assert.InRange(ImagePreprocessor.CalculateAverageLuminance(bitmap), 0.49, 0.51);
    }

    [Fact]
    public void AverageLuminanceUsesRgbWeights()
    {
        using var bitmap = new Bitmap(10, 10);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        Assert.InRange(ImagePreprocessor.CalculateAverageLuminance(bitmap), 0.999, 1.0);
        graphics.Clear(Color.Black);
        Assert.InRange(ImagePreprocessor.CalculateAverageLuminance(bitmap), 0, 0.001);
    }
}
