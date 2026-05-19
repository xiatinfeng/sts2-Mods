using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Modding;
using ModSyncChecker.Scripts.UI;

namespace ModSyncChecker.Scripts;

/// <summary>
/// 单个 MOD 的信息快照
/// </summary>
public class ModInfoSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Path { get; set; } = "";
    public bool HasDll { get; set; }
    public bool HasPck { get; set; }
    public bool AffectsGameplay { get; set; }
    public bool IsEnabled { get; set; }
    public ModLoadState State { get; set; }
    public string? DllHash { get; set; }
    public List<string> Dependencies { get; set; } = new();
    /// <summary>当前游戏加载此 MOD 的顺序序号（0-based）</summary>
    public int LoadOrder { get; set; }
    /// <summary>MOD 来源: 1=本地mods文件夹, 2=Steam Workshop</summary>
    public int ModSource { get; set; }
}

/// <summary>
/// MOD 配置导出/导入格式
/// </summary>
public class ModProfile
{
    public string ProfileName { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public List<ModProfileEntry> Mods { get; set; } = new();
    public List<string> LoadOrder { get; set; } = new();
}

public class ModProfileEntry
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>MOD 来源: 1=本地mods文件夹, 2=Steam Workshop</summary>
    public int Source { get; set; } = 1;
    public int LoadOrder { get; set; }
}

/// <summary>
/// MOD 差异条目
/// </summary>
public class ModDifference
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DifferenceType Type { get; set; }
    public string? LocalVersion { get; set; }
    public string? RemoteVersion { get; set; }
    public string? LocalState { get; set; }
    public string? RemoteState { get; set; }
    public string Details { get; set; } = "";
    /// <summary>本地顺序序号（用于 OrderMismatch）</summary>
    public int? LocalOrder { get; set; }
    /// <summary>远程/配置顺序序号（用于 OrderMismatch）</summary>
    public int? RemoteOrder { get; set; }
    /// <summary>是否为纯工具MOD（不影响玩法），用于 ExtraMod 分组</summary>
    public bool IsToolMod { get; set; }
}

public enum DifferenceType
{
    MissingOnLocal,    // 本地缺失（主机有，本地没有）
    MissingOnRemote,   // 远程缺失（本地有，主机没有）
    VersionMismatch,   // 版本不一致
    StateMismatch,     // 加载状态不一致（如一边Failed一边Loaded）
    HashMismatch,      // DLL/PCK 文件内容不一致
    OrderMismatch,     // 加载顺序不一致
    ExtraMod           // 本地额外MOD（不在配置/远程中，不影响联机同步）
}

/// <summary>
/// v2.7.0: ModSyncChecker runtime configuration loaded from config.json
/// </summary>
public class ModSyncConfig
{
    public float FontScale { get; set; } = 1.0f;
    public string DefaultPanel { get; set; } = "encoding";
    public static readonly ModSyncConfig Default = new();
}

/// <summary>
/// 核心逻辑：扫描、比较、导出导入
/// </summary>
public static class ModSyncCore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static ModSyncConfig? _config;
    private static readonly object _configLock = new();

    /// <summary>
    /// v2.7.0: Runtime configuration, loaded from config.json in ProfileDir.
    /// Returns defaults if config file is missing or malformed.
    /// </summary>
    public static ModSyncConfig Config
    {
        get
        {
            if (_config == null)
            {
                lock (_configLock)
                {
                    _config ??= LoadConfig();
                }
            }
            return _config;
        }
    }

    /// <summary>
    /// v2.7.0: Load config from &lt;ProfileDir&gt;/config.json.
    /// </summary>
    private static ModSyncConfig LoadConfig()
    {
        return LoadConfigFromFile() ?? new ModSyncConfig();
    }

    /// <summary>
    /// v2.8.0: Public accessor for BaseLib integration — loads config.json without fallback.
    /// Returns null if file is missing or malformed, letting callers decide their default.
    /// </summary>
    public static ModSyncConfig? LoadConfigFromFile()
    {
        try
        {
            string configPath = Path.Combine(ProfileDir, "config.json");
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<ModSyncConfig>(json);
                if (config != null)
                {
                    GD.Print($"[ModSyncChecker] Config loaded: font_scale={config.FontScale}, default_panel={config.DefaultPanel}");
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModSyncChecker] Failed to load config.json: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// v2.8.0: Called by BaseLib ModConfig when font_scale changes.
    /// Updates the in-memory config cache and writes config.json.
    /// </summary>
    public static void UpdateFontScale(float value)
    {
        var config = Config; // ensures _config is initialized
        config.FontScale = value;
        SaveConfig();
        GD.Print($"[ModSyncChecker] font_scale updated to {value}");
    }

    /// <summary>
    /// v2.8.0: Called by BaseLib ModConfig when default_panel changes.
    /// </summary>
    public static void UpdateDefaultPanel(string value)
    {
        var config = Config;
        config.DefaultPanel = value;
        SaveConfig();
        GD.Print($"[ModSyncChecker] default_panel updated to {value}");
    }

    /// <summary>
    /// v2.8.0: Persist current in-memory config to config.json.
    /// </summary>
    private static void SaveConfig()
    {
        try
        {
            var config = _config ?? new ModSyncConfig();
            string configPath = Path.Combine(ProfileDir, "config.json");
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModSyncChecker] Failed to save config.json: {ex.Message}");
        }
    }

    /// <summary>
    /// 配置文件存放目录：优先使用游戏安装目录下的 ModSyncChecker 文件夹，
    /// 便于用户手动管理；如果无法获取则降级到 Godot 用户数据目录。
    /// </summary>
    public static string ProfileDir
    {
        get
        {
            try
            {
                string exePath = OS.GetExecutablePath();
                string? gameDir = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir))
                {
                    string dir = Path.Combine(gameDir, "ModSyncChecker");
                    Directory.CreateDirectory(dir);
                    return dir;
                }
            }
            catch { }
            string fallback = Path.Combine(OS.GetUserDataDir(), "ModSyncChecker");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    /// <summary>
    /// 安全读取 mod 的 dependencies 字段。
    /// ModManifest.dependencies 在某些 STS2 版本中不存在（被移除或标记为 internal），
    /// ?. 运算符无法处理字段缺失，运行时会抛出 MissingFieldException。
    /// </summary>
    private static List<string> GetDependenciesSafe(Mod mod)
    {
        try
        {
            return mod.manifest?.dependencies?.ToList() ?? new List<string>();
        }
        catch (MissingFieldException)
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// 扫描当前加载的所有 MOD
    /// </summary>
    public static List<ModInfoSnapshot> ScanLocalMods()
    {
        var result = new List<ModInfoSnapshot>();

        try
        {
            var modsProperty = typeof(ModManager).GetProperty("Mods", BindingFlags.Public | BindingFlags.Static);
            var mods = modsProperty?.GetValue(null) as IReadOnlyList<Mod>;

            if (mods == null)
            {
                GD.PrintErr("[ModSyncChecker] Could not get ModManager.Mods");
                return result;
            }

            for (int i = 0; i < mods.Count; i++)
            {
                var mod = mods[i];
                // 尝试读取 modSource（1=本地mods文件夹, 2=Steam Workshop）
                int modSource = 1;
                try
                {
                    var msField = mod.GetType().GetField("modSource", BindingFlags.Public | BindingFlags.Instance);
                    var msProp = mod.GetType().GetProperty("modSource", BindingFlags.Public | BindingFlags.Instance);
                    if (msField != null)
                        modSource = Convert.ToInt32(msField.GetValue(mod));
                    else if (msProp != null)
                        modSource = Convert.ToInt32(msProp.GetValue(mod));
                }
                catch { }

                var snapshot = new ModInfoSnapshot
                {
                    Id = mod.manifest?.id ?? "unknown",
                    Name = mod.manifest?.name ?? mod.path.GetFile(),
                    Version = mod.manifest?.version ?? "N/A",
                    Author = mod.manifest?.author ?? "unknown",
                    Path = mod.path,
                    HasDll = mod.manifest?.hasDll ?? false,
                    HasPck = mod.manifest?.hasPck ?? false,
                    AffectsGameplay = mod.manifest?.affectsGameplay ?? true,
                    IsEnabled = mod.state != ModLoadState.Disabled,
                    State = mod.state,
                    Dependencies = GetDependenciesSafe(mod),
                    LoadOrder = i,
                    ModSource = modSource
                };

                // 计算 DLL 哈希（如果有）
                if (snapshot.HasDll)
                {
                    string dllPath = Path.Combine(mod.path, snapshot.Id + ".dll");
                    snapshot.DllHash = ComputeFileHash(dllPath);
                }

                result.Add(snapshot);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModSyncChecker] Failed to scan mods: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// 比较本地 MOD 列表与远程 MOD 列表（从连接失败信息中获得）
    /// </summary>
    public static List<ModDifference> CompareMods(
        List<ModInfoSnapshot> localMods,
        List<string> remoteModStrings,
        List<string>? missingOnHost = null,
        List<string>? missingOnLocal = null)
    {
        var differences = new List<ModDifference>();
        var localDict = localMods
            .GroupBy(m => m.Id)
            .ToDictionary(g => g.Key, g =>
            {
                if (g.Count() > 1)
                    GD.PrintErr($"[ModSyncChecker] Duplicate key in dictionary: {g.Key}");
                return g.First();
            });

        // 解析远程 MOD 字符串列表（格式：id-version）
        var remoteDict = new Dictionary<string, string>();
        foreach (var modStr in remoteModStrings)
        {
            int lastDash = modStr.LastIndexOf('-');
            if (lastDash > 0)
            {
                string id = modStr[..lastDash];
                string version = modStr[(lastDash + 1)..];
                remoteDict[id] = version;
            }
            else
            {
                remoteDict[modStr] = "N/A";
            }
        }

        // 1. 检查本地缺失（主机有，本地没有）
        foreach (var (id, version) in remoteDict)
        {
            if (!localDict.ContainsKey(id))
            {
                differences.Add(new ModDifference
                {
                    Id = id,
                    Name = id,
                    Type = DifferenceType.MissingOnLocal,
                    RemoteVersion = version,
                    Details = L.T("MissingLocalDetail")
                });
            }
        }

        // 2. 检查远程缺失（本地有，主机没有）
        foreach (var local in localMods.Where(m => m.AffectsGameplay))
        {
            if (!remoteDict.ContainsKey(local.Id))
            {
                differences.Add(new ModDifference
                {
                    Id = local.Id,
                    Name = local.Name,
                    Type = DifferenceType.MissingOnRemote,
                    LocalVersion = local.Version,
                    LocalState = local.State.ToString(),
                    Details = L.T("MissingRemoteDetail")
                });
            }
        }

        // 3. 检查版本不一致
        foreach (var local in localMods)
        {
            if (remoteDict.TryGetValue(local.Id, out var remoteVersion))
            {
                if (!string.Equals(local.Version, remoteVersion, StringComparison.OrdinalIgnoreCase))
                {
                    differences.Add(new ModDifference
                    {
                        Id = local.Id,
                        Name = local.Name,
                        Type = DifferenceType.VersionMismatch,
                        LocalVersion = local.Version,
                        RemoteVersion = remoteVersion,
                        Details = L.TF("VersionDetailFmt", local.Version, remoteVersion)
                    });
                }
            }
        }

        // 4. 检查加载状态问题
        foreach (var local in localMods)
        {
            if (local.State != ModLoadState.Loaded && local.AffectsGameplay)
            {
                // 如果远程有这个 MOD，但本地没加载成功
                if (remoteDict.ContainsKey(local.Id))
                {
                    differences.Add(new ModDifference
                    {
                        Id = local.Id,
                        Name = local.Name,
                        Type = DifferenceType.StateMismatch,
                        LocalVersion = local.Version,
                        RemoteVersion = remoteDict[local.Id],
                        LocalState = local.State.ToString(),
                        Details = L.TF("StateDetailFmt", local.State)
                    });
                }
            }
        }

        return differences;
    }

    /// <summary>
    /// 检测加载顺序差异（用于配置文件导入对比）
    /// 返回顺序不一致的 MOD 列表，LocalOrder=当前位置，RemoteOrder=配置推荐位置
    /// </summary>
    public static List<ModDifference> CompareLoadOrder(List<ModInfoSnapshot> localMods, ModProfile profile)
    {
        var differences = new List<ModDifference>();

        var localDict = localMods
            .GroupBy(m => $"{m.Id}::{m.ModSource}")
            .ToDictionary(g => g.Key, g =>
            {
                if (g.Count() > 1)
                    GD.PrintErr($"[ModSyncChecker] Duplicate key in dictionary: {g.Key}");
                return g.First();
            });
        var profileDict = profile.Mods
            .GroupBy(m => $"{m.Id}::{m.Source}")
            .ToDictionary(g => g.Key, g =>
            {
                if (g.Count() > 1)
                    GD.PrintErr($"[ModSyncChecker] Duplicate key in dictionary: {g.Key}");
                return g.First();
            });

        // 只比较两边都有的 MOD 的顺序
        var localOrder = localMods
            .Where(m => profileDict.ContainsKey($"{m.Id}::{m.ModSource}"))
            .Select(m => $"{m.Id}::{m.ModSource}")
            .ToList();

        var profileOrder = profile.Mods
            .Where(m => localDict.ContainsKey($"{m.Id}::{m.Source}"))
            .Select(m => $"{m.Id}::{m.Source}")
            .ToList();

        if (localOrder.Count == 0 || profileOrder.Count == 0)
            return differences;

        for (int i = 0; i < profileOrder.Count; i++)
        {
            var key = profileOrder[i];
            var localIdx = localOrder.IndexOf(key);
            if (localIdx != i)
            {
                var parts = key.Split("::");
                var id = parts[0];
                var localMod = localDict[key];

                differences.Add(new ModDifference
                {
                    Id = id,
                    Name = localMod.Name,
                    Type = DifferenceType.OrderMismatch,
                    LocalOrder = localIdx,
                    RemoteOrder = i,
                    Details = L.TF("OrderDetailFmt", localIdx + 1, i + 1)
                });
            }
        }

        return differences;
    }

    /// <summary>
    /// 导出当前 MOD 配置
    /// </summary>
    public static string ExportProfile(string profileName)
    {
        try
        {
            Directory.CreateDirectory(ProfileDir);

            var mods = ScanLocalMods();
            var profile = new ModProfile
            {
                ProfileName = profileName,
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                GameVersion = GetGameVersion(),
                Mods = mods.Select((m, i) => new ModProfileEntry
                {
                    Id = m.Id,
                    Version = m.Version,
                    Enabled = m.IsEnabled,
                    Source = m.ModSource,
                    LoadOrder = i
                }).ToList(),
                LoadOrder = mods.Select(m => $"{m.Id}::{m.ModSource}").ToList()
            };

            string fileName = $"{profileName}_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            string filePath = Path.Combine(ProfileDir, fileName);
            string json = JsonSerializer.Serialize(profile, JsonOptions);
            File.WriteAllText(filePath, json);

            OpenFolderAndSelectFile(filePath);
            GD.Print($"[ModSyncChecker] Profile exported to: {filePath}");
            return filePath;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModSyncChecker] Failed to export profile: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// 获取所有已导出的配置文件
    /// </summary>
    public static List<string> GetExportedProfiles()
    {
        try
        {
            if (!Directory.Exists(ProfileDir))
                return new List<string>();

            return Directory.GetFiles(ProfileDir, "*.json")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// 读取配置文件内容
    /// </summary>
    public static ModProfile? ReadProfile(string filePath)
    {
        try
        {
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<ModProfile>(json);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModSyncChecker] Failed to read profile: {ex.Message}");
            return null;
        }
    }

    private static string? ComputeFileHash(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            using var stream = File.OpenRead(filePath);
            using var md5 = System.Security.Cryptography.MD5.Create();
            byte[] hash = md5.ComputeHash(stream);
            return Convert.ToHexString(hash)[..8];
        }
        catch
        {
            return null;
        }
    }

    // Windows Shell API P/Invoke 声明
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[]? apidl, uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    private const uint SFGAO_FILESYSTEM = 0x40000000;

    /// <summary>
    /// 打开文件所在文件夹并选中该文件（跨平台）
    /// Windows 使用 SHOpenFolderAndSelectItems 原生 API，绕过 explorer.exe 命令行解析问题
    /// </summary>
    public static void OpenFolderAndSelectFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                GD.PrintErr($"[ModSyncChecker] Cannot open folder, file does not exist: {filePath}");
                return;
            }

            string? folder = Path.GetDirectoryName(filePath);

            if (OperatingSystem.IsWindows())
            {
                // 使用 Windows Shell API 直接选中文件，绕过 explorer.exe 的命令行解析
                OpenFolderAndSelectFileWindows(filePath, folder);
            }
            else if (OperatingSystem.IsLinux())
            {
                if (!string.IsNullOrEmpty(folder))
                    Process.Start("xdg-open", folder);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", $"-R \"{filePath}\"");
            }
            else if (OperatingSystem.IsAndroid())
            {
                // Android: use Godot OS.ShellOpen to open file manager
                if (!string.IsNullOrEmpty(folder))
                    OS.ShellOpen(folder);
                else
                    OS.ShellOpen(Path.GetDirectoryName(filePath) ?? filePath);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModSyncChecker] Failed to open folder: {ex.Message}");
            FallbackOpenFolder(filePath);
        }
    }

    /// <summary>
    /// Windows 专用：使用 SHOpenFolderAndSelectItems API 选中文件
    /// </summary>
    private static void OpenFolderAndSelectFileWindows(string filePath, string? folderPath)
    {
        IntPtr pidlFolder = IntPtr.Zero, pidlFile = IntPtr.Zero;
        try
        {
            if (string.IsNullOrEmpty(folderPath))
            {
                FallbackOpenFolder(filePath);
                return;
            }

            // 解析文件夹的 PIDL (Pointer to an Item ID List)
            int hr = SHParseDisplayName(folderPath, IntPtr.Zero, out pidlFolder, SFGAO_FILESYSTEM, out _);
            if (hr != 0)
            {
                GD.PrintErr($"[ModSyncChecker] SHParseDisplayName failed for folder (hr=0x{hr:X8})");
                FallbackOpenFolder(filePath);
                return;
            }

            // 解析文件的 PIDL
            hr = SHParseDisplayName(filePath, IntPtr.Zero, out pidlFile, SFGAO_FILESYSTEM, out _);
            if (hr != 0)
            {
                GD.PrintErr($"[ModSyncChecker] SHParseDisplayName failed for file (hr=0x{hr:X8}), falling back to folder open");
                // 至少打开文件夹
                SHOpenFolderAndSelectItems(pidlFolder, 0, null, 0);
                return;
            }

            // 打开文件夹并选中指定文件（0 = 无特殊标志）
            hr = SHOpenFolderAndSelectItems(pidlFolder, 1, new[] { pidlFile }, 0);
            if (hr != 0)
            {
                GD.PrintErr($"[ModSyncChecker] SHOpenFolderAndSelectItems failed (hr=0x{hr:X8}), falling back");
                FallbackOpenFolder(filePath);
            }
            else
            {
                GD.Print($"[ModSyncChecker] Folder opened and file selected: {Path.GetFileName(filePath)}");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModSyncChecker] Shell API error: {ex.Message}");
            FallbackOpenFolder(filePath);
        }
        finally
        {
            ILFree(pidlFile);
            ILFree(pidlFolder);
        }
    }

    /// <summary>
    /// 降级方案：至少打开文件夹
    /// </summary>
    private static void FallbackOpenFolder(string filePath)
    {
        try
        {
            string? folder = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(folder))
            {
                if (OperatingSystem.IsAndroid())
                {
                    PlatformHelper.ShellOpenFolder(folder);
                }
                else if (Directory.Exists(folder))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = folder,
                        UseShellExecute = true
                    });
                }
                GD.Print($"[ModSyncChecker] Opened folder (fallback): {folder}");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModSyncChecker] Fallback open failed: {ex.Message}");
        }
    }

    private static string GetGameVersion()
    {
        try
        {
            var releaseInfoType = Type.GetType("MegaCrit.Sts2.Core.Debug.ReleaseInfoManager, sts2");
            var instanceProp = releaseInfoType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            var instance = instanceProp?.GetValue(null);
            var releaseInfoProp = instance?.GetType().GetProperty("ReleaseInfo");
            var releaseInfo = releaseInfoProp?.GetValue(instance);
            var versionProp = releaseInfo?.GetType().GetProperty("Version");
            return versionProp?.GetValue(releaseInfo)?.ToString() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    #region 一键应用排序（参考 LoadOrderManager MIT License）

    /// <summary>
    /// v2.6.0: 备份 settings.save 到 settings.save.bak，保护用户数据
    /// </summary>
    public static bool TryBackupSettingsSave(out string error)
    {
        error = string.Empty;
        try
        {
            string? settingsPath = GetSettingsSavePath();
            if (string.IsNullOrEmpty(settingsPath) || !File.Exists(settingsPath))
            {
                error = "Settings save file not found.";
                return false;
            }

            string backupPath = settingsPath + ".bak";
            File.Copy(settingsPath, backupPath, overwrite: true);

            if (File.Exists(backupPath))
            {
                GD.Print($"[ModSyncChecker] Backup saved: {backupPath}");
                return true;
            }

            error = "Backup file not created.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// v2.3.4: 检测 SaveManager 是否可用（用于排序按钮前置检查，避免 ClassName 硬匹配）
    /// </summary>
    public static bool IsSaveManagerAvailable()
    {
        try
        {
            var saveManagerType = Type.GetType("MegaCrit.Sts2.Core.Saves.SaveManager, sts2");
            if (saveManagerType == null) return false;
            var saveManager = saveManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return saveManager != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// v2.6.0: 获取 settings.save 的完整文件系统路径（通过 SaveManager 反射）
    /// </summary>
    private static string? GetSettingsSavePath()
    {
        try
        {
            var saveManagerType = Type.GetType("MegaCrit.Sts2.Core.Saves.SaveManager, sts2");
            if (saveManagerType == null) return null;

            var saveManager = saveManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (saveManager == null) return null;

            var saveStoreField = saveManager.GetType().GetField("_saveStore", BindingFlags.NonPublic | BindingFlags.Instance);
            var saveStore = saveStoreField?.GetValue(saveManager);
            if (saveStore == null) return null;

            var getFullPath = saveStore.GetType().GetMethod("GetFullPath", new[] { typeof(string) });
            return AsString(getFullPath?.Invoke(saveStore, new object[] { "settings.save" }));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 应用配置文件中的加载顺序到游戏 settings.save（下次启动生效）
    /// </summary>
    public static bool TryApplyProfileOrder(ModProfile profile, out string error)
    {
        error = string.Empty;
        try
        {
            // 0. 备份原 settings.save（安全第一）
            if (!TryBackupSettingsSave(out var backupError))
            {
                // 备份失败不影响主流程，仅在日志中记录
                GD.Print($"[ModSyncChecker] Backup skipped: {backupError}");
            }
            else
            {
                GD.Print("[ModSyncChecker] Settings backup created successfully.");
            }

            // 1. 解析配置中的期望顺序
            var expectedOrder = new List<(string Id, int Source, bool Enabled)>();
            var profileEntryDict = profile.Mods
                .GroupBy(m => $"{m.Id}::{m.Source}")
                .ToDictionary(g => g.Key, g =>
                {
                    if (g.Count() > 1)
                        GD.PrintErr($"[ModSyncChecker] Duplicate key in dictionary: {g.Key}");
                    return g.First();
                });

            foreach (var key in profile.LoadOrder)
            {
                if (!TryParseEntryKey(key, out var id, out var source))
                    continue;

                bool enabled = true;
                if (profileEntryDict.TryGetValue(key, out var entry))
                    enabled = entry.Enabled;

                expectedOrder.Add((id, source, enabled));
            }

            // 补充本地有但配置中没有的 MOD（保持当前相对顺序，放末尾）
            var localMods = ScanLocalMods();
            var expectedKeys = expectedOrder.Select(e => $"{e.Id}::{e.Source}").ToHashSet();
            foreach (var local in localMods)
            {
                var key = $"{local.Id}::{local.ModSource}";
                if (!expectedKeys.Contains(key))
                {
                    expectedOrder.Add((local.Id, local.ModSource, local.IsEnabled));
                }
            }

            // 2. 获取 SaveManager
            var saveManagerType = Type.GetType("MegaCrit.Sts2.Core.Saves.SaveManager, sts2");
            if (saveManagerType == null)
            {
                error = "SaveManager type not found.";
                return false;
            }

            var saveManager = saveManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (saveManager == null)
            {
                error = "SaveManager.Instance is null.";
                return false;
            }

            // 3. 用户安全校验
            if (!ValidateUserScope(saveManager, out error))
                return false;

            // 4. 获取或创建 ModSettings
            var settingsSave = GetMemberValue(saveManager, "SettingsSave");
            if (settingsSave == null)
            {
                error = "SaveManager.SettingsSave is null.";
                return false;
            }

            var modSettingsType = Type.GetType("MegaCrit.Sts2.Core.Modding.ModSettings, sts2");
            var settingsSaveModType = Type.GetType("MegaCrit.Sts2.Core.Modding.SettingsSaveMod, sts2");
            var modSourceType = Type.GetType("MegaCrit.Sts2.Core.Modding.ModSource, sts2");

            if (modSettingsType == null || settingsSaveModType == null || modSourceType == null)
            {
                error = "Mod settings runtime types not found.";
                return false;
            }

            var modSettings = GetMemberValue(settingsSave, "ModSettings");
            if (modSettings == null)
            {
                modSettings = Activator.CreateInstance(modSettingsType);
                SetMemberValue(settingsSave, "ModSettings", modSettings);
            }

            // 5. 构建有序 MOD 列表
            var listType = typeof(List<>).MakeGenericType(settingsSaveModType);
            var modList = (System.Collections.IList?)Activator.CreateInstance(listType);
            if (modList == null)
            {
                error = "Failed to create mod list.";
                return false;
            }

            foreach (var entry in expectedOrder)
            {
                var item = Activator.CreateInstance(settingsSaveModType);
                if (item == null) continue;

                SetMemberValue(item, "Id", entry.Id);
                SetMemberValue(item, "Source", Enum.ToObject(modSourceType, entry.Source));
                SetMemberValue(item, "IsEnabled", entry.Enabled);
                modList.Add(item);
            }

            SetMemberValue(modSettings, "ModList", modList);

            // 6. 保存
            var saveSettingsMethod = saveManagerType.GetMethod("SaveSettings", Type.EmptyTypes);
            if (saveSettingsMethod == null)
            {
                error = "SaveManager.SaveSettings() not found.";
                return false;
            }

            saveSettingsMethod.Invoke(saveManager, null);

            // 7. 验证持久化结果
            var persistedKeys = ReadSettingsOrder(saveManager);
            var expectedKeysList = expectedOrder.Select(e => $"{e.Id}::{e.Source}").ToList();
            if (!IsOrderMonotonic(expectedKeysList, persistedKeys))
            {
                error = "Order verification failed after save.";
                return false;
            }

            GD.Print("[ModSyncChecker] Load order applied successfully. Restart required for effect.");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            GD.PrintErr($"[ModSyncChecker] ApplyLoadOrder failed: {ex}");
            return false;
        }
    }

    private static bool ValidateUserScope(object saveManager, out string error)
    {
        error = string.Empty;
        try
        {
            // 获取运行时用户 ID
            var platformUtilType = Type.GetType("MegaCrit.Sts2.Core.Platform.PlatformUtil, sts2");
            var primaryPlatform = platformUtilType?.GetProperty("PrimaryPlatform", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var getLocalPlayerId = platformUtilType?.GetMethod("GetLocalPlayerId");
            var idObj = getLocalPlayerId?.Invoke(null, new[] { primaryPlatform! });
            var runtimeUserId = ToUlong(idObj);

            // 获取 settings.save 路径（_saveStore 是 private 字段，需用 NonPublic 反射）
            var saveStoreField = saveManager.GetType().GetField("_saveStore", BindingFlags.NonPublic | BindingFlags.Instance);
            var saveStore = saveStoreField?.GetValue(saveManager);
            // v2.11.0: saveStore may be unavailable in certain game states without
            // indicating the player is in the wrong place. Skip user-scope validation
            // when we can't resolve the path — TryBackupSettingsSave still backs up.
            if (saveStore == null)
                return true;

            var getFullPath = saveStore.GetType().GetMethod("GetFullPath", new[] { typeof(string) });
            var settingsPath = AsString(getFullPath?.Invoke(saveStore, new object[] { "settings.save" }));

            if (runtimeUserId == null)
            {
                error = "Current runtime user id is unavailable. Refusing save to avoid cross-account overwrite.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(settingsPath))
            {
                error = "Could not resolve settings.save path.";
                return false;
            }

            // 简单校验：路径中是否包含当前用户 ID
            var marker1 = $"/{runtimeUserId}/";
            var marker2 = $"\\{runtimeUserId}\\";
            if (!settingsPath.Contains(marker1, StringComparison.OrdinalIgnoreCase) &&
                !settingsPath.Contains(marker2, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Settings path does not belong to current user {runtimeUserId}. path={settingsPath}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"User scope validation failed: {ex.Message}";
            return false;
        }
    }

    private static List<string> ReadSettingsOrder(object saveManager)
    {
        var keys = new List<string>();
        try
        {
            var settingsSave = GetMemberValue(saveManager, "SettingsSave");
            var modSettings = GetMemberValue(settingsSave, "ModSettings");
            var modListObj = GetMemberValue(modSettings, "ModList");
            if (modListObj is not System.Collections.IEnumerable modList) return keys;

            foreach (var item in modList)
            {
                var id = AsString(GetMemberValue(item, "Id"));
                if (string.IsNullOrWhiteSpace(id)) continue;
                var source = ToInt(GetMemberValue(item, "Source"));
                keys.Add($"{id}::{source}");
            }
        }
        catch (Exception ex) { FileLogger.Warn($"[ModSyncChecker] ReadSettingsOrder failed: {ex.Message}"); }
        return keys;
    }

    private static bool IsOrderMonotonic(List<string> expectedOrder, List<string> persistedOrder)
    {
        var indexMap = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < persistedOrder.Count; i++)
            indexMap.TryAdd(persistedOrder[i], i);

        int prev = -1;
        foreach (var key in expectedOrder)
        {
            if (!indexMap.TryGetValue(key, out var idx))
                continue;
            if (idx < prev)
                return false;
            prev = idx;
        }
        return true;
    }

    private static bool TryParseEntryKey(string key, out string id, out int source)
    {
        id = string.Empty;
        source = 0;
        if (string.IsNullOrWhiteSpace(key)) return false;

        var pos = key.LastIndexOf("::", StringComparison.Ordinal);
        if (pos <= 0 || pos >= key.Length - 2) return false;

        var idPart = key[..pos];
        var sourcePart = key[(pos + 2)..];
        if (string.IsNullOrWhiteSpace(idPart)) return false;
        if (!int.TryParse(sourcePart, out var parsedSource)) return false;

        id = idPart;
        source = parsedSource;
        return true;
    }

    private static object? GetMemberValue(object? obj, string name)
    {
        if (obj == null) return null;
        var type = obj.GetType();
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop != null) return prop.GetValue(obj);
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        return field?.GetValue(obj);
    }

    private static void SetMemberValue(object? obj, string name, object? value)
    {
        if (obj == null) return;
        var type = obj.GetType();
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop != null) { prop.SetValue(obj, value); return; }
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        field?.SetValue(obj, value);
    }

    private static string? AsString(object? obj)
    {
        return obj switch
        {
            null => null,
            string s => s,
            _ => obj.ToString()
        };
    }

    private static ulong? ToUlong(object? obj)
    {
        return obj switch
        {
            null => null,
            ulong u => u,
            long l when l >= 0 => (ulong)l,
            int i when i >= 0 => (ulong)i,
            Enum e => (ulong)Convert.ToInt64(e, System.Globalization.CultureInfo.InvariantCulture),
            _ when ulong.TryParse(obj?.ToString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static int ToInt(object? obj)
    {
        return obj switch
        {
            null => 0,
            int i => i,
            long l => (int)l,
            Enum e => Convert.ToInt32(e, System.Globalization.CultureInfo.InvariantCulture),
            _ when int.TryParse(obj?.ToString(), out var parsed) => parsed,
            _ => 0
        };
    }

    #endregion

    #region 编码排序应用（v2.10.0 — 解析中码的OrderIndex写入settings.save）

    /// <summary>
    /// v2.10.0: Gzip decompress a Base64 string. Returns null on failure.
    /// </summary>
    public static string? GzipDecompress(string base64)
    {
        try
        {
            byte[] compressed = Convert.FromBase64String(base64);
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// v2.10.0: Apply MOD order from a medium code (#MSCv2#gzipBase64#CRC32).
    /// Parses the gzip payload to extract (id, source, orderIdx), then reorders
    /// the Mods array in settings.save by orderIdx.
    /// </summary>
    public static bool TryApplyEncodingOrder(string mediumCode, out string error)
    {
        error = string.Empty;
        try
        {
            // 0. 备份原 settings.save（安全第一）
            if (!TryBackupSettingsSave(out var backupError))
            {
                // 备份失败不影响主流程，仅在日志中记录
                GD.Print($"[ModSyncChecker] Backup skipped: {backupError}");
            }
            else
            {
                GD.Print("[ModSyncChecker] Settings backup created successfully.");
            }

            // 1. Parse medium code
            if (string.IsNullOrEmpty(mediumCode) || !mediumCode.StartsWith("#MSCv2#"))
            {
                error = "Invalid medium code format.";
                return false;
            }

            var afterHeader = mediumCode["#MSCv2#".Length..];
            var hashParts = afterHeader.Split('#');
            if (hashParts.Length < 2)
            {
                error = "Medium code has no compressed payload.";
                return false;
            }

            string base64Payload = hashParts[0];
            var decompressed = GzipDecompress(base64Payload);
            if (decompressed == null)
            {
                error = "Failed to decompress medium code payload.";
                return false;
            }

            // Parse: v2|count|id~ver~source~orderIdx|...|CRC32
            var allParts = decompressed.Split('|');
            if (allParts.Length < 3 || allParts[0] != "v2")
            {
                error = "Unsupported encoding format.";
                return false;
            }

            if (!int.TryParse(allParts[1], out int count))
            {
                error = "Parse error: count field.";
                return false;
            }

            // Extract (id, source, orderIdx) from entries
            var encodedEntries = new List<(string Id, int Source, int OrderIdx)>();
            // allParts[0]=v2, allParts[1]=count, last=CRC32, entries in between
            int entryCount = allParts.Length - 3; // minus v2, count, CRC
            for (int i = 0; i < entryCount && i < count; i++)
            {
                var seg = allParts[i + 2].Split('~');
                if (seg.Length < 4) continue;
                string id = seg[0];
                int.TryParse(seg[2], out int source);
                int.TryParse(seg[3], out int orderIdx);
                encodedEntries.Add((id, source, orderIdx));
            }

            if (encodedEntries.Count == 0)
            {
                error = "No MOD entries found in encoding.";
                return false;
            }

            // Sort encoded entries by orderIdx
            var sortedEncoded = encodedEntries.OrderBy(e => e.OrderIdx).ToList();

            // Build expected order: sorted by orderIdx, then append extra local mods not in encoding
            var localMods = ScanLocalMods();
            var encodedKeySet = encodedEntries.Select(e => $"{e.Id}::{e.Source}").ToHashSet();
            var expectedOrder = new List<(string Id, int Source, bool Enabled)>();

            foreach (var entry in sortedEncoded)
            {
                // Find actual enabled state from local mods
                var localMod = localMods.FirstOrDefault(m =>
                    m.Id == entry.Id && m.ModSource == entry.Source);
                expectedOrder.Add((entry.Id, entry.Source, localMod?.IsEnabled ?? true));
            }

            foreach (var local in localMods)
            {
                var key = $"{local.Id}::{local.ModSource}";
                if (!encodedKeySet.Contains(key))
                    expectedOrder.Add((local.Id, local.ModSource, local.IsEnabled));
            }

            // 2. Get SaveManager
            var saveManagerType = Type.GetType("MegaCrit.Sts2.Core.Saves.SaveManager, sts2");
            if (saveManagerType == null)
            {
                error = "SaveManager type not found.";
                return false;
            }

            var saveManager = saveManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (saveManager == null)
            {
                error = "SaveManager.Instance is null.";
                return false;
            }

            // 3. User scope validation
            if (!ValidateUserScope(saveManager, out error))
                return false;

            // 4. Get or create ModSettings
            var settingsSave = GetMemberValue(saveManager, "SettingsSave");
            if (settingsSave == null)
            {
                error = "SaveManager.SettingsSave is null.";
                return false;
            }

            var modSettingsType = Type.GetType("MegaCrit.Sts2.Core.Modding.ModSettings, sts2");
            var settingsSaveModType = Type.GetType("MegaCrit.Sts2.Core.Modding.SettingsSaveMod, sts2");
            var modSourceType = Type.GetType("MegaCrit.Sts2.Core.Modding.ModSource, sts2");

            if (modSettingsType == null || settingsSaveModType == null || modSourceType == null)
            {
                error = "Mod settings runtime types not found.";
                return false;
            }

            var modSettings = GetMemberValue(settingsSave, "ModSettings");
            if (modSettings == null)
            {
                modSettings = Activator.CreateInstance(modSettingsType);
                SetMemberValue(settingsSave, "ModSettings", modSettings);
            }

            // 5. Build ordered MOD list
            var listType = typeof(List<>).MakeGenericType(settingsSaveModType);
            var modList = (System.Collections.IList?)Activator.CreateInstance(listType);
            if (modList == null)
            {
                error = "Failed to create mod list.";
                return false;
            }

            foreach (var entry in expectedOrder)
            {
                var item = Activator.CreateInstance(settingsSaveModType);
                if (item == null) continue;

                SetMemberValue(item, "Id", entry.Id);
                SetMemberValue(item, "Source", Enum.ToObject(modSourceType, entry.Source));
                SetMemberValue(item, "IsEnabled", entry.Enabled);
                modList.Add(item);
            }

            SetMemberValue(modSettings, "ModList", modList);

            // 6. Save
            var saveSettingsMethod = saveManagerType.GetMethod("SaveSettings", Type.EmptyTypes);
            if (saveSettingsMethod == null)
            {
                error = "SaveManager.SaveSettings() not found.";
                return false;
            }

            saveSettingsMethod.Invoke(saveManager, null);

            // 7. Verify persistence
            var persistedKeys = ReadSettingsOrder(saveManager);
            var expectedKeysList = expectedOrder.Select(e => $"{e.Id}::{e.Source}").ToList();
            if (!IsOrderMonotonic(expectedKeysList, persistedKeys))
            {
                error = "Order verification failed after save.";
                return false;
            }

            GD.Print("[ModSyncChecker] Encoding order applied successfully. Restart required.");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            GD.PrintErr($"[ModSyncChecker] TryApplyEncodingOrder failed: {ex}");
            return false;
        }
    }

    #endregion
}
