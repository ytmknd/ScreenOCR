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
