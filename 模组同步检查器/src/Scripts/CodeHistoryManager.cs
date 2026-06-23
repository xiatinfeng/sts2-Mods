using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ModSyncChecker.Scripts;

public class CodeHistoryEntry
{
    public string Code { get; set; } = "";
    public string ShortLabel { get; set; } = "";
    public string Timestamp { get; set; } = "";
}

/// <summary>
/// v2.10.5: 编码历史持久化管理器。
/// 最近 3 条编码存到 ProfileDir/code_history.json，跨会话恢复。
/// </summary>
public class CodeHistoryManager
{
    private const int MaxEntries = 3;
    private readonly string _filePath;
    private List<CodeHistoryEntry> _entries = new();

    public IReadOnlyList<CodeHistoryEntry> Entries => _entries;

    public CodeHistoryManager()
    {
        _filePath = Path.Combine(ModSyncCore.ProfileDir, "code_history.json");
        Load();
    }

    public void Add(string code, string shortLabel)
    {
        // 去重
        _entries.RemoveAll(e => string.Equals(e.Code, code, StringComparison.Ordinal));

        _entries.Insert(0, new CodeHistoryEntry
        {
            Code = code,
            ShortLabel = shortLabel,
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        });

        while (_entries.Count > MaxEntries)
            _entries.RemoveAt(_entries.Count - 1);

        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return;

            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<List<CodeHistoryEntry>>(json);
            if (loaded != null)
                _entries = loaded;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[ModSyncChecker] Failed to load code history: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[ModSyncChecker] Failed to save code history: {ex.Message}");
        }
    }
}
