namespace ScreenOCR;

[Flags]
public enum HotKeyModifiers : uint
{
    None = 0, Alt = 0x0001, Control = 0x0002, Shift = 0x0004, Win = 0x0008, NoRepeat = 0x4000
}

public readonly record struct HotKeyGesture(HotKeyModifiers Modifiers, uint VirtualKey, string DisplayText);

public static class HotKeyParser
{
    public static bool TryParse(string? value, out HotKeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string[] parts = value.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(string.IsNullOrEmpty)) return false;

        HotKeyModifiers modifiers = HotKeyModifiers.NoRepeat;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!seen.Add(parts[i])) return false;
            HotKeyModifiers parsed = parts[i].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotKeyModifiers.Control,
                "ALT" => HotKeyModifiers.Alt,
                "SHIFT" => HotKeyModifiers.Shift,
                "WIN" or "WINDOWS" => HotKeyModifiers.Win,
                _ => HotKeyModifiers.None
            };
            if (parsed == HotKeyModifiers.None) return false;
            modifiers |= parsed;
        }

        if ((modifiers & ~HotKeyModifiers.NoRepeat) == 0 || !TryParseKey(parts[^1], out uint key)) return false;
        gesture = new HotKeyGesture(modifiers, key, value.Trim());
        return true;
    }

    public static HotKeyGesture Parse(string value) => TryParse(value, out HotKeyGesture result)
        ? result : throw new FormatException($"ホットキーの形式が不正です: {value}");

    private static bool TryParseKey(string text, out uint key)
    {
        key = 0;
        string upper = text.ToUpperInvariant();
        if (upper.Length == 1 && (char.IsAsciiLetterUpper(upper[0]) || char.IsAsciiDigit(upper[0])))
        {
            key = upper[0];
            return true;
        }
        if (upper.StartsWith('F') && int.TryParse(upper.AsSpan(1), out int f) && f is >= 1 and <= 24)
        {
            key = (uint)(0x70 + f - 1);
            return true;
        }
        return false;
    }
}

public sealed class HotKeyRegistration : IDisposable
{
    private const int Id = 0x534F;
    private IntPtr _window;
    public bool Register(IntPtr window, HotKeyGesture gesture)
    {
        Unregister();
        if (!Native.RegisterHotKey(window, Id, (uint)gesture.Modifiers, gesture.VirtualKey)) return false;
        _window = window;
        return true;
    }
    public bool IsMessage(ref Message message) => message.Msg == Native.WmHotKey && message.WParam.ToInt32() == Id;
    public void Unregister()
    {
        if (_window == IntPtr.Zero) return;
        Native.UnregisterHotKey(_window, Id);
        _window = IntPtr.Zero;
    }
    public void Dispose() => Unregister();
}
