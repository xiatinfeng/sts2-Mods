using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using Sts2SpawnCheat.Patches;
using Sts2SpawnCheat.UI;

namespace Sts2SpawnCheat.Core;

/// <summary>
/// ModConfig 软依赖集成（来自 联机/ModConfig 模组）。
/// 注册配置面板 + F5 快捷键 + 蒸汽云存档 + 屏蔽模组检测。
/// </summary>
public static class ModConfigIntegration
{
    private const string ModId = "sts2-spawn-cheat";

    private static bool _detected;
    private static bool _available;
    private static Type? _apiType;
    private static Type? _entryType;
    private static Type? _configType;

    /// <summary>是否隐藏模组（联机时客机看不到）</summary>
    public static bool HideFromModList { get; set; } = true;

    private static Action? _toggleAction;
    private static int _frameCounter;

    public static bool IsAvailable
    {
        get
        {
            if (!_detected)
            {
                _detected = true;
                _apiType = Type.GetType("ModConfig.ModConfigApi, ModConfig");
                _entryType = Type.GetType("ModConfig.ConfigEntry, ModConfig");
                _configType = Type.GetType("ModConfig.ConfigType, ModConfig");
                if (_apiType == null || _entryType == null || _configType == null)
                    FindTypesInAssemblies();
                _available = _apiType != null && _entryType != null && _configType != null;
                CardRewardPatch.DiagLog($"ModConfig IsAvailable={_available} api={_apiType != null} entry={_entryType != null} type={_configType != null}");
            }
            return _available;
        }
    }

    private static void FindTypesInAssemblies()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (_apiType == null) _apiType = asm.GetType("ModConfig.ModConfigApi");
                if (_entryType == null) _entryType = asm.GetType("ModConfig.ConfigEntry");
                if (_configType == null) _configType = asm.GetType("ModConfig.ConfigType");
                if (_apiType != null && _entryType != null && _configType != null) break;
            }
            catch { }
        }
    }

    /// <summary>注册 ModConfig 配置项。延迟两帧。</summary>
    public static void TryRegister(Action togglePanel)
    {
        _toggleAction = togglePanel;
        if (!IsAvailable) return;

        CardRewardPatch.DiagLog("ModConfig found, scheduling registration via ProcessFrame...");
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null) { CardRewardPatch.DiagLog("SceneTree null"); return; }

        _frameCounter = 0;
        tree.ProcessFrame += OnProcessFrame;
    }

    private static void OnProcessFrame()
    {
        _frameCounter++;
        if (_frameCounter < 2) return;

        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree != null) tree.ProcessFrame -= OnProcessFrame;
        DoRegister();
    }

    private static void DoRegister()
    {
        CardRewardPatch.DiagLog("DoRegister starting...");
        try
        {
            var list = new List<object>();

            list.Add(MakeHeader("Hidden Spawn Cheat", "隐藏生成作弊器"));

            // F5 快捷键
            list.Add(MakeKeyBind("spawncheat_toggle", "Toggle Panel", "开/关面板",
                (long)Key.F5, v =>
                {
                    try
                    {
                        if (v != null)
                        {
                            var keyCode = Convert.ToInt64(v);
                            SpawnCheatPanel.ToggleKey = (Key)keyCode;
                            CardRewardPatch.DiagLog($"Hotkey changed to {(Key)keyCode}");
                        }
                    }
                    catch { }
                }));

            // 屏蔽模组检测
            list.Add(MakeToggle("hide_from_mod_list", "Hide from Mod List", "屏蔽作弊模组检测",
                "When enabled, this mod is removed from the mod list sent to clients.",
                "开启时，从联机 Mod 列表中移除本模组。",
                true, v =>
                {
                    try { HideFromModList = Convert.ToBoolean(v); }
                    catch { }
                }));

            // 调试日志开关
            list.Add(MakeToggle("debug_logging", "Debug Logging", "调试日志",
                "Log detailed debug info to spawn-cheat-debug.log.",
                "记录详细调试信息到 spawn-cheat-debug.log。",
                false, v =>
                {
                    try { CardRewardPatch.DebugLoggingEnabled = Convert.ToBoolean(v); }
                    catch { }
                }));

            list.Add(MakeSeparator());
            list.Add(MakeHeader("Host-only mod.", "仅房主需安装，客机无需安装。"));

            var arr = Array.CreateInstance(_entryType, list.Count);
            for (int i = 0; i < list.Count; i++) arr.SetValue(list[i], i);

            var register = _apiType.GetMethod("Register",
                new[] { typeof(string), typeof(string), _entryType.MakeArrayType() });
            if (register != null)
            {
                register.Invoke(null, [ModId, "Hidden Spawn Cheat / 隐藏生成作弊器", arr]);
                CardRewardPatch.DiagLog("ModConfig registration complete.");
            }
            else
                CardRewardPatch.DiagLog("Register method not found.");

            // 从云端加载已保存的值
            SyncFromConfig();
        }
        catch (Exception e)
        {
            CardRewardPatch.DiagLog($"DoRegister failed: {e.GetType().Name}: {e.Message}");
        }
    }

    // ── 蒸汽云存档 ────────────────────────────────────────────────────

    private static void SyncFromConfig()
    {
        if (!IsAvailable) return;
        try
        {
            // 隐藏模组开关（Toggle 类型，走 GetValue/SetValue）
            HideFromModList = GetValue<bool>("hide_from_mod_list", true);

            // 调试日志开关
            CardRewardPatch.DebugLoggingEnabled = GetValue<bool>("debug_logging", false);

            CardRewardPatch.DiagLog($"SyncFromConfig: hide={HideFromModList} debug={CardRewardPatch.DebugLoggingEnabled}");
        }
        catch (Exception e)
        {
            CardRewardPatch.DiagLog($"SyncFromConfig failed: {e.Message}");
        }
    }

    private static T GetValue<T>(string key, T fallback)
    {
        if (!IsAvailable) return fallback;
        try
        {
            var method = _apiType.GetMethod("GetValue").MakeGenericMethod(typeof(T));
            return (T)method.Invoke(null, [ModId, key]);
        }
        catch { return fallback; }
    }

    private static void SetValue(string key, object value)
    {
        if (!IsAvailable) return;
        try
        {
            var method = _apiType.GetMethod("SetValue");
            method?.Invoke(null, [ModId, key, value]);
        }
        catch { }
    }

    // ── 辅助构造方法 ──────────────────────────────────────────────────

    private static object ConfigTypeValue(string name) => Enum.Parse(_configType, name);

    private static Dictionary<string, string> Dict(string k1, string v1, string k2, string v2)
        => new() { [k1] = v1, [k2] = v2 };

    private static void SetProp(object obj, string name, object value)
        => obj.GetType().GetProperty(name)?.SetValue(obj, value);

    private static object MakeHeader(string labelEn, string labelZhs)
    {
        var e = Activator.CreateInstance(_entryType);
        SetProp(e, "Label", labelEn);
        SetProp(e, "Labels", Dict("en", labelEn, "zhs", labelZhs));
        SetProp(e, "Type", ConfigTypeValue("Header"));
        return e;
    }

    private static object MakeSeparator()
    {
        var e = Activator.CreateInstance(_entryType);
        SetProp(e, "Type", ConfigTypeValue("Separator"));
        return e;
    }

    private static object MakeToggle(string key, string labelEn, string labelZhs,
        string descEn = null, string descZhs = null,
        bool defaultValue = true, Action<object> onChanged = null)
    {
        var e = Activator.CreateInstance(_entryType);
        SetProp(e, "Key", key);
        SetProp(e, "Label", labelEn);
        SetProp(e, "Labels", Dict("en", labelEn, "zhs", labelZhs));
        SetProp(e, "Type", ConfigTypeValue("Toggle"));
        SetProp(e, "DefaultValue", defaultValue);
        if (descEn != null || descZhs != null)
        {
            SetProp(e, "Description", descEn ?? descZhs);
            SetProp(e, "Descriptions", Dict("en", descEn ?? "", "zhs", descZhs ?? ""));
        }
        if (onChanged != null) SetProp(e, "OnChanged", onChanged);
        return e;
    }

    private static object MakeKeyBind(string key, string labelEn, string labelZhs,
        long defaultValue, Action<object> onChanged = null)
    {
        var e = Activator.CreateInstance(_entryType);
        SetProp(e, "Key", key);
        SetProp(e, "Label", labelEn);
        SetProp(e, "Labels", Dict("en", labelEn, "zhs", labelZhs));
        SetProp(e, "Type", ConfigTypeValue("KeyBind"));
        SetProp(e, "DefaultValue", defaultValue);
        SetProp(e, "Description", "Press the key to rebind.");
        SetProp(e, "Descriptions", Dict("en", "Press the key to rebind.", "zhs", "按下按键重新绑定。"));
        if (onChanged != null) SetProp(e, "OnChanged", onChanged);
        return e;
    }
}
