using System.Text;

namespace ScreenOCR;

public sealed class Logger
{
    private readonly object _gate = new();
    private readonly string _directory;
    public Logger(int retentionDays)
    {
        _directory = Path.Combine(AppConfigStore.DirectoryPath, "logs");
        Directory.CreateDirectory(_directory);
        Rotate(retentionDays);
    }
    public string DirectoryPath => _directory;
    public void Info(string message) => Write("INFO", message);
    public void Warning(string message) => Write("WARN", message);
    public void Error(string message, Exception? exception = null) => Write("ERROR", exception is null ? message : $"{message}: {exception}");
    private void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (_gate) File.AppendAllText(Path.Combine(_directory, $"screenocr-{DateTime.Now:yyyyMMdd}.log"), line, new UTF8Encoding(false));
    }
    private void Rotate(int days)
    {
        DateTime cutoff = DateTime.Now.Date.AddDays(-Math.Max(1, days));
        foreach (string file in Directory.EnumerateFiles(_directory, "screenocr-*.log"))
        {
            try { if (File.GetLastWriteTime(file) < cutoff) File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
