using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace ModSyncChecker.Scripts.UI;

/// <summary>
/// Modular localization system for ModSyncChecker.
/// Loads translations from i18n/*.json files. Falls back to embedded English and Chinese.
/// Supports community-contributed translations by dropping new .json files into the i18n folder.
/// </summary>
public static class L
{
    private static readonly Dictionary<string, Dictionary<string, string>> _translations = new();
    private static string _currentLang = "en";
    private static readonly List<string> _availableLanguages = new();

    static L()
    {
        LoadAllTranslations();
        DetectLanguage();
    }

    /// <summary>
    /// Load all translation files from the i18n folder.
    /// If no external files exist, falls back to embedded English and Chinese.
    /// </summary>
    private static void LoadAllTranslations()
    {
        _translations.Clear();
        _availableLanguages.Clear();

        // Try to load from i18n folder (allows community translations)
        string i18nDir = GetI18nDirectory();
        if (Directory.Exists(i18nDir))
        {
            var jsonFiles = Directory.GetFiles(i18nDir, "*.json");
            FileLogger.Info($"[ModSyncChecker i18n] LoadAllTranslations: i18nDir={i18nDir}, found {jsonFiles.Length} .json file(s): [{string.Join(", ", jsonFiles.Select(Path.GetFileName))}]");
            foreach (var file in jsonFiles)
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("_meta", out var meta)) continue;
                    if (!meta.TryGetProperty("code", out var codeProp)) continue;

                    string langCode = codeProp.GetString() ?? Path.GetFileNameWithoutExtension(file);
                    var dict = new Dictionary<string, string>();

                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Name.StartsWith("_")) continue;
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            dict[prop.Name] = prop.Value.GetString() ?? prop.Name;
                        }
                    }

                    _translations[langCode] = dict;
                    _availableLanguages.Add(langCode);
                    FileLogger.Info($"[ModSyncChecker] Loaded translation: {langCode} from {Path.GetFileName(file)}");
                }
                catch (Exception ex)
                {
                    FileLogger.Error($"[ModSyncChecker] Failed to load translation {file}: {ex.Message}");
                }
            }
        }

        // Ensure English fallback is always available
        if (!_translations.ContainsKey("en"))
        {
            _translations["en"] = GetEmbeddedEnglish();
            _availableLanguages.Add("en");
        }

        // Ensure Chinese is available if not loaded from file
        if (!_translations.ContainsKey("zh"))
        {
            _translations["zh"] = GetEmbeddedChinese();
            _availableLanguages.Add("zh");
        }

        // Diagnostic: print key counts for all loaded languages
        var counts = string.Join(", ", _translations.Select(kv => $"{kv.Key}={kv.Value.Count} keys"));
        FileLogger.Info($"[ModSyncChecker i18n] LoadAllTranslations done — {_translations.Count} language(s): [{counts}]");
    }

    /// <summary>
    /// Get the i18n directory path. Tries mod folder first, then user data dir.
    /// </summary>
    private static string GetI18nDirectory()
    {
        try
        {
            string? modDir = Path.GetDirectoryName(typeof(L).Assembly.Location);
            FileLogger.Info($"[ModSyncChecker i18n] Assembly.Location dir: {modDir ?? "null"}");
            if (!string.IsNullOrEmpty(modDir))
            {
                string dir = Path.Combine(modDir, "i18n");
                FileLogger.Info($"[ModSyncChecker i18n] Trying path: {dir}  exists={Directory.Exists(dir)}");
                if (Directory.Exists(dir)) return dir;
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error($"[ModSyncChecker i18n] GetI18nDirectory error: {ex.Message}");
        }

        string fallback = Path.Combine(OS.GetUserDataDir(), "ModSyncChecker", "i18n");
        FileLogger.Info($"[ModSyncChecker i18n] Fallback path: {fallback}  exists={Directory.Exists(fallback)}");
        return fallback;
    }

    /// <summary>
    /// Auto-detect game language: TranslationServer (game setting) > OS locale (system fallback).
    /// v1.2.3 behavior: the panel language follows the game's language setting.
    /// v2.11.0: Stop treating "en" as a sentinel — respect the game's locale choice.
    /// Only fall back to OS locale when TranslationServer returns truly empty/null.
    /// </summary>
    private static void DetectLanguage()
    {
        try
        {
            // Primary: game's own locale (switches when player changes language in settings)
            string locale = TranslationServer.GetLocale();
            
            // Fallback: only if game hasn't set a locale, use OS
            if (string.IsNullOrEmpty(locale))
            {
                string osLocale = OS.GetLocale();
                if (!string.IsNullOrEmpty(osLocale))
                    locale = osLocale;
            }

            string detected = locale.ToLowerInvariant() switch
            {
                var s when s.StartsWith("zh") => "zh",
                var s when s.StartsWith("ja") => "ja",
                var s when s.StartsWith("ko") => "ko",
                var s when s.StartsWith("fr") => "fr",
                var s when s.StartsWith("de") => "de",
                var s when s.StartsWith("es") => "es",
                var s when s.StartsWith("ru") => "ru",
                var s when s.StartsWith("pt") => "pt",
                _ => "en"
            };

            if (_translations.ContainsKey(detected))
                _currentLang = detected;
            else if (_translations.ContainsKey("en"))
                _currentLang = "en";
        }
        catch
        {
            _currentLang = "en";
        }
    }

    public static string T(string key)
    {
        if (_translations.TryGetValue(_currentLang, out var dict) && dict.TryGetValue(key, out var value))
            return value;
        if (_translations.TryGetValue("en", out var enDict) && enDict.TryGetValue(key, out var enValue))
            return enValue;
        return key;
    }

    public static string TF(string key, params object[] args)
    {
        return string.Format(T(key), args);
    }

    public static void SetLanguage(string lang)
    {
        if (_translations.ContainsKey(lang))
        {
            _currentLang = lang;
            FileLogger.Info($"[ModSyncChecker] Language set to: {lang}");
        }
        else
        {
            FileLogger.Error($"[ModSyncChecker] Language '{lang}' not available.");
        }
    }

    public static string CurrentLanguage => _currentLang;

    public static IReadOnlyList<string> AvailableLanguages => _availableLanguages;

    public static string GetLanguageName(string code)
    {
        if (_translations.TryGetValue(code, out var dict) && dict.TryGetValue("_meta_language", out var name))
            return name;
        return code.ToUpperInvariant();
    }

    public static void Reload()
    {
        LoadAllTranslations();
        DetectLanguage();
        FileLogger.Info($"[ModSyncChecker] Translations reloaded (language={_currentLang}).");
    }

    #region Embedded Fallbacks

    private static Dictionary<string, string> GetEmbeddedEnglish() => new()
    {
        // Panel
        ["Title"] = "MOD Sync Checker",
        ["Refresh"] = "Refresh",
        ["Export"] = "Export",
        ["Import"] = "Import",
        ["ApplyOrder"] = "Apply Order",
        ["ProfileLabel"] = "Profile:",
        ["SearchPlaceholder"] = "Search profiles...",
        ["Close"] = "X",
        ["DeleteProfileTooltip"] = "Delete selected profile",
        ["DeleteIcon"] = "🗑",

        // v1.3.0: Code mode
        ["GenerateCode"] = "Generate Code",
        ["CompareCode"] = "Compare",
        ["EmptyInput"] = "Paste a code first!",
        ["InvalidCodeFormat"] = "Invalid code format (expected MSCv2)",
        ["NoResult"] = "No comparison results",
        ["ParseError"] = "Failed to parse code",
        ["CodeParseError"] = "Parse error: invalid sync code format",
        ["CodeHashOk"] = "Hash verified ✓",
        ["CodeHashMismatch"] = "Hash mismatch — mod lists differ",
        ["DiffSource"] = "Source Mismatch",
        ["CodeSynced"] = "Synced",
        ["CodeMissing"] = "Missing",
        ["CodeExtra"] = "Extra",
        ["CodeVersionMismatch"] = "Version Mismatch",
        ["CodeMatched"] = "Matched",
        ["CodeCopyReport"] = "Copy Report",
        ["CodeEmpty"] = "Please enter a sync code first",
        ["CodeGenerated"] = "Code copied to input. Share with teammates!",
        ["CodeNotSyncedExpand"] = "Not synced, expand details",
        ["CodeCompareHint"] = "Paste a sync code below to compare MOD lists",
        ["PasteCodeHere"] = "Paste sync code here...",
        ["CodeHistoryPlaceholder"] = "-- Recent Codes (History) --",
        ["TabEncoding"] = "Encoding",
        ["TabImport"] = "Import",

        // v2.2.0: Copy diff button
        ["CopyDiff"] = "Copy Diff",
        ["CopyDiffSuccess"] = "Diff report copied to clipboard!",

        // Summary
        ["SummaryTotal"] = "Total: {0} | Loaded: {1} | Failed: {2} | Disabled: {3}",
        ["HintRefresh"] = "Press ESC to close | Green=Loaded Red=Failed Gray=Disabled",
        ["NoDiff"] = "All MODs are in sync!",
        ["AllSynced"] = "All MODs synchronized",

        // Table headers
        ["HeaderName"] = "MOD Name",
        ["HeaderState"] = "State",
        ["HeaderVersion"] = "Version",
        ["HeaderType"] = "Difference",
        ["HeaderDetails"] = "Details",

        // Groups
        ["GroupCritical"] = "Critical Differences (Affects Multiplayer) - {0}",
        ["GroupOrder"] = "Load Order Differences - {0}",
        ["GroupExtraGameplay"] = "Extra MODs (Gameplay-affecting, suspect first) - {0}",
        ["GroupExtraTool"] = "Extra Tool MODs (Safe) - {0}",
        ["GroupPassed"] = "Passed Check - {0}",

        // States
        ["StateLoaded"] = "Loaded",
        ["StateDisabled"] = "Disabled",
        ["StateFailed"] = "Failed",
        ["Passed"] = "Passed",
        ["VersionFmt"] = "v{0} [{1}]",

        // Diff types
        ["DiffMissingLocal"] = "Missing (You)",
        ["DiffMissingRemote"] = "Missing (Host)",
        ["DiffVersion"] = "Version Mismatch",
        ["DiffState"] = "State Error",
        ["DiffHash"] = "File Mismatch",
        ["DiffOrder"] = "Order Mismatch",
        ["DiffExtra"] = "Extra MOD",
        ["DiffUnknown"] = "Unknown",

        // Diff details
        ["MissingLocalDetail"] = "Host has this MOD, you don't",
        ["MissingRemoteDetail"] = "You have this MOD, host doesn't",
        ["VersionDetailFmt"] = "Version mismatch: yours {0} vs host {1}",
        ["StateDetailFmt"] = "MOD not loaded: {0}",
        ["OrderDetailFmt"] = "Current #{0} → Suggested #{1}",
        ["ExtraGameplayDetail"] = "Extra MOD (not in config)",
        ["ExtraToolDetail"] = "Extra tool MOD (no gameplay impact)",
        ["ProfileRequiredFmt"] = "Config requires this MOD (v{0})",
        ["ProfileVersionFmt"] = "Config requires v{0}, yours is v{1}",

        // v2.6.0: Apply order with confirmation
        ["ApplyOrderConfirmTitle"] = "Apply MOD Order",
        ["ApplyOrderConfirmText"] = "This will write the load order from profile '{0}' into setting.save.\nThe original file will be backed up as .bak.\nA game restart is required. Continue?",
        ["ApplyOrderConfirmYes"] = "Apply",
        ["ApplyOrderConfirmNo"] = "Cancel",

        // v2.10.0: Apply encoding order
        ["ApplyEncodingOrder"] = "Apply This Order",
        ["ApplyEncodingOrderConfirmTitle"] = "Apply Encoding Order",
        ["ApplyEncodingOrderConfirmText"] = "This will parse the MOD order from the encoding and write it to setting.save.\nThe original file will be backed up automatically.\nA game restart is required. Continue?",

        // Export/Import/Delete
        ["ExportSuccessFmt"] = "Exported: {0}, folder opened",
        ["ExportFail"] = "Export failed!",
        ["ImportSuccessFmt"] = "Imported: {0}",
        ["ImportFailFmt"] = "Import failed: {0}",
        ["NoProfile"] = "No profile to apply",
        ["ApplyOrderSuccess"] = "Order applied! Restart required",
        ["ApplyOrderFailFmt"] = "Apply order failed: {0}",
        ["ApplyOrderNeedGameStart"] = "Please start a run (enter the map) before applying load order",
        ["CompareProfileFmt"] = "Comparing profile: {0} ({1})",
        ["DeleteConfirmTitle"] = "Delete Profile",
        ["DeleteConfirmText"] = "Are you sure you want to delete '{0}'?\nThis action cannot be undone.",
        ["DeleteConfirmYes"] = "Delete",
        ["DeleteConfirmNo"] = "Cancel",
        ["DeleteSuccessFmt"] = "Deleted: {0}",
        ["DeleteFailFmt"] = "Delete failed: {0}",

        // Summary stats
        ["SummaryMissingLocal"] = "Missing {0}",
        ["SummaryMissingRemote"] = "Host missing {0}",
        ["SummaryVersion"] = "Version diff {0}",
        ["SummaryState"] = "State error {0}",
        ["SummaryOrder"] = "Order diff {0}",
        ["SummaryExtraGameplay"] = "Extra {0}",
        ["SummaryExtraTool"] = "Tool {0}",
        ["SummaryDiffCount"] = "Differences: {0}",

        // Hints
        ["HintColors"] = "Red=You miss | Orange=Host misses | Yellow=Version diff | Gold=Extra | Gray=Tool | Cyan=Order | Green=Passed",
        ["SelectProfile"] = "(Select profile to compare)",

        // Main menu
        ["MainMenuButton"] = "MOD SYNC",

        // Init messages (English debug)
        ["InitSuccess"] = "[ModSyncChecker] Initialized successfully!",
        ["PanelCreated"] = "[ModSyncChecker] Panel created! Press K to open.",
        ["PatchApplied"] = "[ModSyncChecker] Harmony patches applied.",
        ["PatchFailedFmt"] = "[ModSyncChecker] Harmony patch failed: {0}",
        ["PanelCreateFailedFmt"] = "[ModSyncChecker] Failed to create panel: {0}",
        ["MenuButtonAdded"] = "[ModSyncChecker] Main menu button added successfully.",
        ["ConnectionFailedFmt"] = "[ModSyncChecker] Detected connection failure: {0}",
    };

    private static Dictionary<string, string> GetEmbeddedChinese() => new()
    {
        // Panel
        ["Title"] = "MOD 同步检测器",
        ["Refresh"] = "刷新检测",
        ["Export"] = "导出配置",
        ["Import"] = "导入配置",
        ["ApplyOrder"] = "应用排序",
        ["ProfileLabel"] = "配置文件:",
        ["SearchPlaceholder"] = "搜索配置文件...",
        ["Close"] = "X",
        ["DeleteProfileTooltip"] = "删除选中的配置文件",
        ["DeleteIcon"] = "🗑",

        // v1.3.0: 代码模式
        ["GenerateCode"] = "生成编码",
        ["CompareCode"] = "对比",
        ["EmptyInput"] = "请先粘贴编码！",
        ["InvalidCodeFormat"] = "编码格式无效 (需要 MSCv2)",
        ["NoResult"] = "未发现对比结果",
        ["ParseError"] = "解析编码失败",
        ["CodeParseError"] = "解析失败: 无效的同步码格式",
        ["CodeHashOk"] = "哈希验证通过 ✓",
        ["CodeHashMismatch"] = "哈希不匹配 — MOD 列表不一致",
        ["DiffSource"] = "来源不同",
        ["CodeSynced"] = "已同步",
        ["CodeMissing"] = "缺失",
        ["CodeExtra"] = "多余",
        ["CodeVersionMismatch"] = "版本不一致",
        ["CodeMatched"] = "匹配",
        ["CodeCopyReport"] = "复制报告",
        ["CodeEmpty"] = "请先输入同步码",
        ["CodeGenerated"] = "编码已生成！分享给队友吧！",
        ["CodeNotSyncedExpand"] = "不一致，展开详情",
        ["CodeCompareHint"] = "粘贴同步编码来对比 MOD 列表",
        ["PasteCodeHere"] = "在此粘贴同步编码...",
        ["CodeHistoryPlaceholder"] = "— 最近编码 (历史) —",
        ["TabEncoding"] = "编码",
        ["TabImport"] = "导入",

        // v2.2.0: 一键复制差异
        ["CopyDiff"] = "复制差异",
        ["CopyDiffSuccess"] = "差异报告已复制到剪贴板！",

        // Summary
        ["SummaryTotal"] = "总计: {0} | 正常: {1} | 失败: {2} | 禁用: {3}",
        ["HintRefresh"] = "按 ESC 关闭 | 正常=绿色 失败=红色 禁用=灰色",
        ["NoDiff"] = "MOD 配置完全一致！",
        ["AllSynced"] = "所有 MOD 已同步",

        // Table headers
        ["HeaderName"] = "MOD 名称",
        ["HeaderState"] = "状态",
        ["HeaderVersion"] = "版本",
        ["HeaderType"] = "差异类型",
        ["HeaderDetails"] = "详情",

        // Groups
        ["GroupCritical"] = "关键差异（影响联机同步）- {0} 个",
        ["GroupOrder"] = "加载顺序差异 - {0} 个",
        ["GroupExtraGameplay"] = "额外 MOD（影响玩法，排查时优先怀疑）- {0} 个",
        ["GroupExtraTool"] = "额外工具MOD（不影响玩法）- {0} 个",
        ["GroupPassed"] = "已通过检测 - {0} 个",

        // States
        ["StateLoaded"] = "正常",
        ["StateDisabled"] = "禁用",
        ["StateFailed"] = "失败",
        ["Passed"] = "已通过",
        ["VersionFmt"] = "v{0} [{1}]",

        // Diff types
        ["DiffMissingLocal"] = "本地缺失",
        ["DiffMissingRemote"] = "主机缺失",
        ["DiffVersion"] = "版本不同",
        ["DiffState"] = "状态异常",
        ["DiffHash"] = "文件不同",
        ["DiffOrder"] = "顺序不同",
        ["DiffExtra"] = "额外MOD",
        ["DiffUnknown"] = "未知",

        // Diff details
        ["MissingLocalDetail"] = "主机有此 MOD，但你没有安装",
        ["MissingRemoteDetail"] = "你有此 MOD，但主机没有",
        ["VersionDetailFmt"] = "版本不一致：你 {0} vs 主机 {1}",
        ["StateDetailFmt"] = "MOD 未正常加载：{0}",
        ["OrderDetailFmt"] = "当前 #{0} → 建议 #{1}",
        ["ExtraGameplayDetail"] = "本地额外MOD（不在配置中）",
        ["ExtraToolDetail"] = "本地额外工具MOD（不影响玩法）",
        ["ProfileRequiredFmt"] = "配置要求此 MOD (v{0})",
        ["ProfileVersionFmt"] = "配置要求 v{0}，你的是 v{1}",

        // v2.6.0: 一键同步排序确认
        ["ApplyOrderConfirmTitle"] = "应用 MOD 排序",
        ["ApplyOrderConfirmText"] = "将把配置文件 '{0}' 中的 MOD 排序写入 setting.save，原文件将备份为 .bak。\n重启游戏后生效，确定继续？",
        ["ApplyOrderConfirmYes"] = "确定应用",
        ["ApplyOrderConfirmNo"] = "取消",

        // v2.10.0: 应用编码排序
        ["ApplyEncodingOrder"] = "应用此排序",
        ["ApplyEncodingOrderConfirmTitle"] = "应用编码排序",
        ["ApplyEncodingOrderConfirmText"] = "将从编码解析 MOD 排序并写入 setting.save，原文件自动备份。\n重启游戏后生效，确定继续？",

        // Export/Import/Delete
        ["ExportSuccessFmt"] = "已导出: {0}，文件夹已打开",
        ["ExportFail"] = "导出失败！",
        ["ImportSuccessFmt"] = "已导入: {0}",
        ["ImportFailFmt"] = "导入失败: {0}",
        ["NoProfile"] = "没有可应用的配置",
        ["ApplyOrderSuccess"] = "排序已应用！重启游戏后生效",
        ["ApplyOrderFailFmt"] = "应用排序失败: {0}",
        ["ApplyOrderNeedGameStart"] = "请先开始一局游戏（进入地图）后再应用排序",
        ["CompareProfileFmt"] = "对比配置文件: {0} ({1})",
        ["DeleteConfirmTitle"] = "删除配置文件",
        ["DeleteConfirmText"] = "确定要删除 '{0}' 吗？\n此操作不可撤销。",
        ["DeleteConfirmYes"] = "删除",
        ["DeleteConfirmNo"] = "取消",
        ["DeleteSuccessFmt"] = "已删除: {0}",
        ["DeleteFailFmt"] = "删除失败: {0}",

        // Summary stats
        ["SummaryMissingLocal"] = "缺失 {0}",
        ["SummaryMissingRemote"] = "主机缺 {0}",
        ["SummaryVersion"] = "版本不同 {0}",
        ["SummaryState"] = "状态异常 {0}",
        ["SummaryOrder"] = "顺序不同 {0}",
        ["SummaryExtraGameplay"] = "额外MOD {0}",
        ["SummaryExtraTool"] = "工具MOD {0}",
        ["SummaryDiffCount"] = "差异: {0}",

        // Hints
        ["HintColors"] = "红色=你缺MOD | 橙色=主机缺MOD | 黄色=版本不同 | 金色=额外MOD | 灰色=工具MOD | 青色=顺序不同 | 绿色=已通过",
        ["SelectProfile"] = "(选择配置文件导入对比)",

        // Main menu
        ["MainMenuButton"] = "MOD SYNC",

        // Init messages (English debug)
        ["InitSuccess"] = "[ModSyncChecker] Initialized successfully!",
        ["PanelCreated"] = "[ModSyncChecker] Panel created! Press K to open.",
        ["PatchApplied"] = "[ModSyncChecker] Harmony patches applied.",
        ["PatchFailedFmt"] = "[ModSyncChecker] Harmony patch failed: {0}",
        ["PanelCreateFailedFmt"] = "[ModSyncChecker] Failed to create panel: {0}",
        ["MenuButtonAdded"] = "[ModSyncChecker] Main menu button added successfully.",
        ["ConnectionFailedFmt"] = "[ModSyncChecker] Detected connection failure: {0}",
    };

    #endregion
}
