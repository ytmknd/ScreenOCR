using System.Text;
using System.Text.RegularExpressions;

namespace ScreenOCR;

public readonly record struct RectF(float X, float Y, float Width, float Height)
{
    public float Left => X;
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;
}
public sealed record OcrWordInfo(string Text, RectF Bounds);
public sealed record OcrLineInfo(IReadOnlyList<OcrWordInfo> Words);

public sealed class TextProcessingOptions
{
    public bool RemoveCjkSpaces { get; init; } = true;
    public bool SortLines { get; init; } = true;
    public bool JoinLines { get; init; }
    public bool CollapseSpaces { get; init; } = true;
    public bool NormalizeFullWidth { get; init; }
    public bool HalfToFullKana { get; init; }
    public IReadOnlyList<ReplacementRule> Replacements { get; init; } = [];
}

public static partial class TextPostProcessor
{
    private const string JoinStopCharacters = "。．！？!?」』）)：:；;・…";

    public static string Process(IReadOnlyList<OcrLineInfo> input, TextProcessingOptions? options = null)
    {
        options ??= new();
        IEnumerable<IndexedLine> lines = input.Select((line, index) => new IndexedLine(line, index, GetBounds(line)));
        if (options.SortLines) lines = StableSort(lines);
        var built = lines.Select(x => BuildLine(x.Line, options.RemoveCjkSpaces).TrimEnd()).ToList();
        string text = options.JoinLines ? JoinLines(built) : string.Join("\n", built);
        if (options.CollapseSpaces) text = MultipleSpacesRegex().Replace(text, " ");
        text = TrimBlankEdges(NormalizeNewlines(text));
        if (options.NormalizeFullWidth) text = NormalizeFullWidth(text);
        if (options.HalfToFullKana) text = ConvertHalfWidthKana(text);
        foreach (ReplacementRule rule in options.Replacements.Where(x => x.Enabled && x.IsValid)) text = rule.Apply(text);
        return NormalizeNewlines(text);
    }

    public static bool IsCjk(char value) => value is
        >= '\u3000' and <= '\u303F' or >= '\u3040' and <= '\u309F' or
        >= '\u30A0' and <= '\u30FF' or >= '\u31F0' and <= '\u31FF' or
        >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF' or
        >= '\uF900' and <= '\uFAFF' or >= '\uFF00' and <= '\uFF60' or
        >= '\uFF61' and <= '\uFF9F' or >= '\uFFE0' and <= '\uFFE6';

    private static string BuildLine(OcrLineInfo line, bool removeCjkSpaces)
    {
        if (line.Words.Count == 0) return string.Empty;
        double characterCount = line.Words.Sum(x => x.Text.Length);
        double refWidth = characterCount > 0 ? line.Words.Sum(x => x.Bounds.Width) / characterCount : double.NaN;
        var result = new StringBuilder(line.Words[0].Text);
        for (int i = 1; i < line.Words.Count; i++)
        {
            OcrWordInfo previous = line.Words[i - 1];
            OcrWordInfo next = line.Words[i];
            if (NeedsSpace(previous, next, removeCjkSpaces, refWidth)) result.Append(' ');
            result.Append(next.Text);
        }
        return result.ToString();
    }

    private static bool NeedsSpace(OcrWordInfo previous, OcrWordInfo next, bool removeCjkSpaces, double refWidth)
    {
        if (!removeCjkSpaces) return true;
        if (previous.Text.Length == 0 || next.Text.Length == 0) return true;
        if (IsCjk(previous.Text[^1]) || IsCjk(next.Text[0])) return false;
        if (double.IsNaN(refWidth) || refWidth <= 0) return true;
        return next.Bounds.Left - previous.Bounds.Right > refWidth * 0.35;
    }

    private static IEnumerable<IndexedLine> StableSort(IEnumerable<IndexedLine> source)
    {
        var remaining = source.OrderBy(x => x.Bounds.Top).ThenBy(x => x.Index).ToList();
        var output = new List<IndexedLine>(remaining.Count);
        while (remaining.Count > 0)
        {
            IndexedLine anchor = remaining[0];
            float band = Math.Max(1, anchor.Bounds.Height * 0.5f);
            var sameBand = remaining.Where(x => Math.Abs(x.Bounds.Top - anchor.Bounds.Top) < band)
                .OrderBy(x => x.Bounds.Left).ThenBy(x => x.Index).ToList();
            output.AddRange(sameBand);
            foreach (IndexedLine item in sameBand) remaining.Remove(item);
        }
        return output;
    }

    private static RectF GetBounds(OcrLineInfo line)
    {
        if (line.Words.Count == 0) return default;
        float left = line.Words.Min(x => x.Bounds.Left);
        float top = line.Words.Min(x => x.Bounds.Top);
        float right = line.Words.Max(x => x.Bounds.Right);
        float bottom = line.Words.Max(x => x.Bounds.Bottom);
        return new(left, top, right - left, bottom - top);
    }

    private static string JoinLines(IReadOnlyList<string> lines)
    {
        var result = new StringBuilder();
        foreach (string line in lines)
        {
            if (result.Length == 0) { result.Append(line); continue; }
            if (line.Length == 0 || result[^1] == '\n') { result.Append('\n').Append(line); continue; }
            char previous = result[^1];
            if (JoinStopCharacters.Contains(previous)) result.Append('\n');
            else if (!IsCjk(previous) || !IsCjk(line[0])) result.Append(' ');
            result.Append(line);
        }
        return result.ToString();
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);

    private static string TrimBlankEdges(string value)
    {
        string[] lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int start = 0, end = lines.Length;
        while (start < end && string.IsNullOrWhiteSpace(lines[start])) start++;
        while (end > start && string.IsNullOrWhiteSpace(lines[end - 1])) end--;
        return string.Join("\r\n", lines[start..end]);
    }

    private static string NormalizeFullWidth(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (char c in value) result.Append(c switch
        {
            '\u3000' => ' ', >= '\uFF01' and <= '\uFF5E' => (char)(c - 0xFEE0), _ => c
        });
        return result.ToString();
    }

    private static string ConvertHalfWidthKana(string value)
    {
        var result = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length;)
        {
            if (value[i] is < '\uFF61' or > '\uFF9F')
            {
                result.Append(value[i++]);
                continue;
            }
            int start = i;
            while (i < value.Length && value[i] is >= '\uFF61' and <= '\uFF9F') i++;
            result.Append(value[start..i].Normalize(NormalizationForm.FormKC));
        }
        return result.ToString();
    }

    [GeneratedRegex(" {2,}")]
    private static partial Regex MultipleSpacesRegex();
    private sealed record IndexedLine(OcrLineInfo Line, int Index, RectF Bounds);
}
