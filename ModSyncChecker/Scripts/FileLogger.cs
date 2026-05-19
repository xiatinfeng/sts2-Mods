using System;
using System.IO;
using Godot;

namespace ModSyncChecker.Scripts;

/// <summary>
/// 文件日志系统 — 写入游戏 logs/ 目录
/// 同时输出到 GD.Print 便于控制台查看
/// </summary>
public static class FileLogger
{
    private static string? _logPath;
    private static readonly object _lock = new();

    public static string LogPath => _logPath ?? "(未初始化)";

    public static void Init()
    {
        if (_logPath != null) return;

        lock (_lock)
        {
            if (_logPath != null) return;

            try
            {
                var logDir = Path.Combine(OS.GetUserDataDir(), "logs");
                Directory.CreateDirectory(logDir);
                _logPath = Path.Combine(logDir, "ModSyncChecker_debug.log");
            }
            catch
            {
                // 降级：AppData
                try
                {
                    var logDir = Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                        "SlayTheSpire2", "logs");
                    Directory.CreateDirectory(logDir);
                    _logPath = Path.Combine(logDir, "ModSyncChecker_debug.log");
                }
                catch
                {
                    _logPath = null;
                    return;
                }
            }

            WriteHeader();
            Info("FileLogger initialized.");
        }
    }

    private static void WriteHeader()
    {
        if (_logPath == null) return;
        try
        {
            File.AppendAllText(_logPath,
                $"\n{'='*60}\n" +
                $"  ModSyncChecker v2.4.3 Started\n" +
                $"  Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"  Log: {_logPath}\n" +
                $"{'='*60}\n");
        }
        catch (Exception ex) { GD.PrintErr($"[ModSyncChecker] Failed to write log header: {ex.Message}"); }
    }

    public static void Info(string msg)  => Write("INFO ", msg);
    public static void Warn(string msg)  => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Debug(string msg) => Write("DEBUG", msg);

    private static void Write(string level, string msg)
    {
        GD.Print($"[ModSyncChecker] [{level}] {msg}");

        if (_logPath == null) return;
        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {msg}\n");
            }
            catch (Exception ex) { GD.PrintErr($"[ModSyncChecker] Failed to write log: {ex.Message}"); }
        }
    }
}
