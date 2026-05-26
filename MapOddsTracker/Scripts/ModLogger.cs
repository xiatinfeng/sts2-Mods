using System;
using System.IO;
using Godot;

namespace MapOddsTracker.Scripts;

/// <summary>
/// Thin wrapper around GD.Print / GD.PrintErr that also writes to a dedicated log file.
/// Log file: %APPDATA%/SlayTheSpire2/MapOddsTracker.log
/// </summary>
internal static class ModLogger
{
    private static readonly string LogFilePath;

    static ModLogger()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var logDir = Path.Combine(appData, "SlayTheSpire2");
        Directory.CreateDirectory(logDir);
        LogFilePath = Path.Combine(logDir, "MapOddsTracker.log");
    }

    public static void Log(string message)
    {
        var line = $"{ModConstants.LogPrefix} {message}";
        GD.Print(line);
        AppendToFile(line);
    }

    public static void LogErr(string message)
    {
        var line = $"{ModConstants.LogPrefix} {message}";
        GD.PrintErr(line);
        AppendToFile($"[ERR] {line}");
    }

    private static void AppendToFile(string line)
    {
        try
        {
            File.AppendAllText(LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch
        {
            // Silently ignore file write failures — GD.Print is the primary output
        }
    }
}
