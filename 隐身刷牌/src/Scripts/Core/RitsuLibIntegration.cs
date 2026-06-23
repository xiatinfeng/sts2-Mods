using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Godot;
using Sts2SpawnCheat.Patches;

namespace Sts2SpawnCheat.Core;

/// <summary>
/// RitsuLib 软依赖集成。
/// 若 ritsulib 已加载，通过其 RuntimeHotkeyService 注册全局热键 F5。
/// 若未加载，完全不影响原有功能。
/// </summary>
public static class RitsuLibIntegration
{
    private const string RitsuModId = "STS2-RitsuLib";
    private static string LogPath => CardRewardPatch.DebugLogPath;

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
    }

    public static bool IsAvailable { get; private set; }

    /// <summary>尝试将 toggleCallback 注册为 F5 热键</summary>
    public static void TryRegisterF5(Action toggleCallback)
    {
        try
        {
            var ritsuAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == RitsuModId);
            if (ritsuAsm == null)
            {
                Log("RitsuLib not loaded, skipping hotkey registration.");
                return;
            }

            IsAvailable = true;
            Log("RitsuLib found, registering F5 hotkey...");

            var svc = ritsuAsm.GetType("STS2RitsuLib.RuntimeInput.RuntimeHotkeyService");
            if (svc == null) { Log("RuntimeHotkeyService type not found"); return; }
            Log("RuntimeHotkeyService found.");

            // Initialize
            var initMethod = svc.GetMethod("Initialize", Type.EmptyTypes);
            if (initMethod == null) { Log("Initialize() not found"); } else { initMethod.Invoke(null, null); Log("Initialize() called."); }

            // 构造 RuntimeHotkeyOptions
            var optType = ritsuAsm.GetType("STS2RitsuLib.RuntimeInput.RuntimeHotkeyOptions");
            var txtType = ritsuAsm.GetType("STS2RitsuLib.RuntimeInput.RuntimeHotkeyText");
            if (optType == null) { Log("RuntimeHotkeyOptions not found"); return; }
            if (txtType == null) { Log("RuntimeHotkeyText not found"); return; }
            Log("Options types found.");

            var opts = Activator.CreateInstance(optType)!;
            optType.GetProperty("Id")?.SetMethod?.Invoke(opts, ["sts2-spawn-cheat-toggle"]);

            var literal = txtType.GetMethod("Literal", [typeof(string)]);
            if (literal != null)
            {
                var displayName = literal.Invoke(null, ["Hidden Spawn Cheat"]);
                optType.GetProperty("DisplayName")?.SetMethod?.Invoke(opts, [displayName]);
            }
            Log("Options created.");

            // 列出所有 Register 方法
            var allRegisters = svc.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Register").ToList();
            Log($"Found {allRegisters.Count} Register methods:");
            foreach (var rm in allRegisters)
            {
                var ps = string.Join(", ", rm.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                Log($"  Register({ps})");
            }

            // 精确查找 Register(string, Action, RuntimeHotkeyOptions)
            var register = svc.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Register" &&
                                     m.GetParameters().Length == 3 &&
                                     m.GetParameters()[0].ParameterType == typeof(string) &&
                                     m.GetParameters()[1].ParameterType == typeof(Action));

            if (register == null) { Log("Register(string,Action,?) not found!"); return; }

            Log($"Register method found: {register}");
            Log($"Param types: {string.Join(", ", register.GetParameters().Select(p => p.ParameterType.FullName))}");

            var handle = register.Invoke(null, ["F5", toggleCallback, opts]);
            Log($"F5 registered! Handle type: {handle?.GetType().FullName ?? "NULL"}");

            // 验证：列出已注册热键
            var getHotkeys = svc.GetMethod("GetRegisteredHotkeys", Type.EmptyTypes);
            if (getHotkeys != null)
            {
                var hotkeys = getHotkeys.Invoke(null, null);
                var countProp = hotkeys?.GetType().GetProperty("Count");
                if (countProp != null)
                {
                    var count = countProp.GetValue(hotkeys);
                    Log($"Current registered hotkeys: {count}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"FAILED: {ex.GetType().Name}: {ex.Message}");
            Log($"Stack: {ex.StackTrace}");
        }
    }
}
