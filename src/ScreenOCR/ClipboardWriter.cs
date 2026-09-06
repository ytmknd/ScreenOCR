using System.Runtime.InteropServices;
using System.Text;

namespace ScreenOCR;

public static class ClipboardWriter
{
    public static bool TryWrite(IntPtr owner, string text, Bitmap? image = null)
    {
        IntPtr textMemory = Allocate(Encoding.Unicode.GetBytes(text + '\0'));
        IntPtr imageMemory = image is null ? IntPtr.Zero : Allocate(CreateDib(image));
        if (textMemory == IntPtr.Zero || (image is not null && imageMemory == IntPtr.Zero))
        {
            Free(ref textMemory); Free(ref imageMemory); return false;
        }

        bool opened = false;
        for (int attempt = 0; attempt < 10 && !opened; attempt++)
        {
            opened = Native.OpenClipboard(owner);
            if (!opened && attempt < 9) Thread.Sleep(100);
        }
        if (!opened) { Free(ref textMemory); Free(ref imageMemory); return false; }

        try
        {
            if (!Native.EmptyClipboard()) return false;
            if (Native.SetClipboardData(Native.CfUnicodeText, textMemory) == IntPtr.Zero) return false;
            textMemory = IntPtr.Zero;
            if (imageMemory != IntPtr.Zero)
            {
                if (Native.SetClipboardData(Native.CfDib, imageMemory) == IntPtr.Zero) return false;
                imageMemory = IntPtr.Zero;
            }
            return true;
        }
        finally
        {
            Native.CloseClipboard();
            Free(ref textMemory); Free(ref imageMemory);
        }
    }

    private static IntPtr Allocate(byte[] bytes)
    {
        IntPtr memory = Native.GlobalAlloc(Native.GmemMoveable, (nuint)bytes.Length);
        if (memory == IntPtr.Zero) return IntPtr.Zero;
        IntPtr target = Native.GlobalLock(memory);
        if (target == IntPtr.Zero) { Native.GlobalFree(memory); return IntPtr.Zero; }
        Marshal.Copy(bytes, 0, target, bytes.Length);
        Native.GlobalUnlock(memory);
        return memory;
    }

    private static byte[] CreateDib(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Bmp);
        byte[] bmp = stream.ToArray();
        return bmp[14..];
    }

    private static void Free(ref IntPtr memory)
    {
        if (memory == IntPtr.Zero) return;
        Native.GlobalFree(memory);
        memory = IntPtr.Zero;
    }
}
