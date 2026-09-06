using System.ComponentModel;
using System.Drawing.Imaging;

namespace ScreenOCR;

public sealed class FrozenSnapshot : IDisposable
{
    public FrozenSnapshot(Bitmap bitmap, Rectangle virtualBounds)
    {
        Bitmap = bitmap;
        VirtualBounds = virtualBounds;
    }
    public Bitmap Bitmap { get; }
    public Rectangle VirtualBounds { get; }

    public Bitmap Crop(Rectangle screenRectangle)
    {
        Rectangle clipped = Rectangle.Intersect(screenRectangle, VirtualBounds);
        if (clipped.Width <= 0 || clipped.Height <= 0) throw new ArgumentOutOfRangeException(nameof(screenRectangle));
        var local = new Rectangle(clipped.X - VirtualBounds.X, clipped.Y - VirtualBounds.Y, clipped.Width, clipped.Height);
        return Bitmap.Clone(local, PixelFormat.Format32bppArgb);
    }
    public void Dispose() => Bitmap.Dispose();
}

public static class ScreenCapture
{
    public static Rectangle GetVirtualBounds() => new(
        Native.GetSystemMetrics(Native.SmXVirtualScreen), Native.GetSystemMetrics(Native.SmYVirtualScreen),
        Native.GetSystemMetrics(Native.SmCxVirtualScreen), Native.GetSystemMetrics(Native.SmCyVirtualScreen));

    public static FrozenSnapshot CaptureVirtualDesktop()
    {
        Rectangle bounds = GetVirtualBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("仮想デスクトップのサイズを取得できませんでした。");
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new Win32Exception("画面 DC を取得できませんでした。");
        IntPtr memoryDc = IntPtr.Zero, hBitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            memoryDc = Native.CreateCompatibleDC(screenDc);
            hBitmap = Native.CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
            if (memoryDc == IntPtr.Zero || hBitmap == IntPtr.Zero) throw new Win32Exception();
            old = Native.SelectObject(memoryDc, hBitmap);
            if (!Native.BitBlt(memoryDc, 0, 0, bounds.Width, bounds.Height, screenDc, bounds.X, bounds.Y, Native.Srccopy | Native.CaptureBlt)) throw new Win32Exception();
            using Image image = Image.FromHbitmap(hBitmap);
            return new FrozenSnapshot(new Bitmap(image), bounds);
        }
        finally
        {
            if (old != IntPtr.Zero && memoryDc != IntPtr.Zero) Native.SelectObject(memoryDc, old);
            if (hBitmap != IntPtr.Zero) Native.DeleteObject(hBitmap);
            if (memoryDc != IntPtr.Zero) Native.DeleteDC(memoryDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static float GetDpiPercent(Rectangle rectangle)
    {
        var point = new Native.NativePoint(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2);
        IntPtr monitor = Native.MonitorFromPoint(point, 2);
        if (monitor != IntPtr.Zero && Native.GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0)
        {
            return dpiX * 100f / 96f;
        }
        return 100f;
    }
}
