namespace ScreenOCR.Tests;

public sealed class PpOcrV6SmallServiceTests
{
    [Fact]
    public void ParseResultConvertsRapidOcrOutput()
    {
        const string json = """
        {
          "engine": "PP-OCRv6 Small",
          "elapsedSeconds": 0.42,
          "lines": [
            {
              "text": "ScreenOCR 2026",
              "confidence": 0.98,
              "boundingBox": [[10, 20], [150, 20], [150, 40], [10, 40]]
            },
            {
              "text": "日本語も認識",
              "confidence": 0.96,
              "boundingBox": [[10, 50], [130, 50], [130, 75], [10, 75]]
            }
          ]
        }
        """;

        OcrPipelineResult result = PpOcrV6SmallService.ParseResult(json, new AppConfig(), elapsedMilliseconds: 321);

        Assert.Equal(OcrFailure.None, result.Failure);
        Assert.Equal($"ScreenOCR 2026{Environment.NewLine}日本語も認識", result.Text);
        Assert.Equal("ja+en", result.LanguageTag);
        Assert.Equal("PPv6", result.SelectedAttempt);
        Assert.Equal(321, result.ElapsedMilliseconds);
        Assert.Equal(new RectF(10, 20, 140, 20), result.Lines[0].Words[0].Bounds);
        Assert.False(result.MergeInfo!.Attempted);
    }

    [Fact]
    public void ParseResultReturnsNoTextForEmptyLines()
    {
        OcrPipelineResult result = PpOcrV6SmallService.ParseResult("{\"lines\":[]}", new AppConfig());

        Assert.Equal(OcrFailure.NoTextFound, result.Failure);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public void ParseResultReportsRecOnlyFallback()
    {
        const string json = """
        {
          "engine": "PP-OCRv6 Small",
          "mode": "rec-only",
          "scale": 3.0,
          "margin": 16,
          "lines": [
            { "text": "Hello", "confidence": 0.99, "boundingBox": [[0, 0], [35, 0], [35, 14], [0, 14]] }
          ]
        }
        """;

        OcrPipelineResult result = PpOcrV6SmallService.ParseResult(json, new AppConfig());

        Assert.Equal(OcrFailure.None, result.Failure);
        Assert.Equal("Hello", result.Text);
        Assert.Equal("PPv6-rec", result.SelectedAttempt);
        Assert.Equal(3.0, result.Scale);
        Assert.Equal("PPv6-rec", Assert.Single(result.Attempts).Name);
    }

    [Fact]
    public void ParseResultDefaultsToDetectModeAndUnitScale()
    {
        const string json = """
        {
          "lines": [
            { "text": "Hello", "confidence": 0.99, "boundingBox": [[0, 0], [35, 0], [35, 14], [0, 14]] }
          ]
        }
        """;

        OcrPipelineResult result = PpOcrV6SmallService.ParseResult(json, new AppConfig());

        Assert.Equal("PPv6", result.SelectedAttempt);
        Assert.Equal(1, result.Scale);
    }

    [Fact]
    public void ParseResultRejectsMissingLines()
    {
        Assert.Throws<System.Text.Json.JsonException>(() =>
            PpOcrV6SmallService.ParseResult("{}", new AppConfig()));
    }
}
