using System.Diagnostics;

namespace ClaudeUsage;

/// <summary>
/// Simple debug logger that writes timestamped lines to %TEMP%\claudeusage-debug.log
/// Only writes when %TEMP%\claudeusage-debug.on flag file exists.
/// </summary>
internal static class DebugLog
{
    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(),
        "claudeusage-debug.log");

    private static readonly string FlagPath = Path.Combine(
        Path.GetTempPath(),
        "claudeusage-debug.on");

    public static void WriteLine(string message)
    {
        if (!File.Exists(FlagPath))
        {
            return;
        }

        try
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var line = $"[{timestamp}] {message}";
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch
        {
            // Logging can never break the extension
        }
    }
}
