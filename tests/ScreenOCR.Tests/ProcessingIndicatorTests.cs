using System.Drawing;

namespace ScreenOCR.Tests;

public sealed class ProcessingIndicatorTests
{
    [Fact]
    public void CalculateLocationCentersIndicatorInSelection()
    {
        Point location = ProcessingIndicator.CalculateLocation(
            new Rectangle(100, 200, 600, 300),
            new Rectangle(0, 0, 1920, 1080),
            new Size(300, 82));

        Assert.Equal(new Point(250, 309), location);
    }

    [Fact]
    public void CalculateLocationKeepsIndicatorInsideWorkingArea()
    {
        Point location = ProcessingIndicator.CalculateLocation(
            new Rectangle(1870, 1040, 40, 30),
            new Rectangle(0, 0, 1920, 1080),
            new Size(300, 82));

        Assert.Equal(new Point(1620, 998), location);
    }
}
