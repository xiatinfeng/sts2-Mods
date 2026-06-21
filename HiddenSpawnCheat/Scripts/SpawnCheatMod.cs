using Godot;
using HarmonyLib;
using System;
using System.IO;
using System.Reflection;
using Sts2SpawnCheat.Core;
using Sts2SpawnCheat.Patches;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Modding;
using Sts2SpawnCheat.UI;

namespace Sts2SpawnCheat;

/// <summary>
/// MOD 入口。手动注册 Harmony 补丁（不使用 PatchAll，避免 TargetMethod null 异常）。
/// 
/// 初始化失败时通过反射写入 ModLogPlus 日志（不依赖 GD.Print 可见性）。
/// </summary>
[ModInitializer("Initialize")]
public static class SpawnCheatMod
{
    private static Harmony? _harmony;

    public static void Initialize()
    {
        try
        {
            GD.Print("[SpawnCheat] Initializing v0.1.0...");

            _harmony = new Harmony("com.xiatingfeng.sts2spawncheat");

            // 手动注册所有补丁
            RegisterCardRewardPatches();
            RegisterCardRewardRerollPatch();
            RegisterMerchantRefreshPatch();

            // 遗物/药水奖励拦截
            RelicRewardPatch.Register(_harmony!);
            PotionRewardPatch.Register(_harmony!);

            CardSpawnService.InitCollections();
            CardSpawnService.LoadEnchantOrder();

            RegisterNGameReadyPatch();

            GD.Print("[SpawnCheat] Harmony patches applied. Press F5 in-game to open panel.");
        }
        catch (Exception ex)
        {
            // 写 ModLogPlus 日志（反射调用，不影响初始化流程）
            var msg = CardRewardPatch.SanitizePath($"{ex.GetType()}: {ex.Message}\n{ex.StackTrace}");
            if (ex.InnerException != null)
                msg = CardRewardPatch.SanitizePath($"{msg}\nInner: {ex.InnerException.GetType()}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}");

            var json = JsonEscape(msg);

            WriteModLogPlus("ERROR", json);

            // 也写 GD.PrintErr（给 Godot 控制台）
            GD.PrintErr($"[SpawnCheat] INIT FAILED: {ex.GetType()}: {ex.Message}");
            GD.PrintErr($"[SpawnCheat] StackTrace: {CardRewardPatch.SanitizePath(ex.StackTrace ?? "")}");
            if (ex.InnerException != null)
                GD.PrintErr($"[SpawnCheat] Inner: {ex.InnerException.GetType()}: {CardRewardPatch.SanitizePath(ex.InnerException.Message ?? "")}");

            throw; // 让游戏知道初始化失败
        }
    }

    private static void RegisterCardRewardPatches()
    {
        var tCardReward = AccessTools.TypeByName("MegaCrit.Sts2.Core.Rewards.CardReward");
        if (tCardReward == null)
        {
            GD.PrintErr("[SpawnCheat] CardReward type not found, skipping card patches");
            return;
        }

        // Patch 1: Populate — 队列消耗式顶替
        var populate = AccessTools.Method(tCardReward, "Populate");
        if (populate != null)
        {
            _harmony!.Patch(populate,
                postfix: new HarmonyMethod(typeof(CardRewardPatch.PopulatePatch),
                    nameof(CardRewardPatch.PopulatePatch.Postfix)));
            GD.Print("[SpawnCheat] Populate patch registered");
        }
        else GD.PrintErr("[SpawnCheat] Populate method not found, skipping");
    }

    private static void RegisterCardRewardRerollPatch()
    {
        CardRewardRerollPatch.Register(_harmony!);
    }

    private static void RegisterMerchantRefreshPatch()
    {
        MerchantRefreshPatch.Register(_harmony!);
    }

    private static void RegisterNGameReadyPatch()
    {
        var nGame = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.NGame");
        if (nGame == null) return;

        var ready = AccessTools.Method(nGame, "_Ready");
        if (ready == null) return;

        _harmony!.Patch(ready,
            postfix: new HarmonyMethod(typeof(NGameReadyMethods), nameof(NGameReadyMethods.OnReady)));
    }

    // ═══════════════════════════════════════════════════
    //  ModLogPlus 集成（反射调用，不依赖编译时引用）
    // ═══════════════════════════════════════════════════

    private static void WriteModLogPlus(string level, string jsonMessage)
    {
        try
        {
            // 通过反射找 ModLogPlus.JsonLinesWriter 类型
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!asm.GetName().Name.Contains("ModLogPlus")) continue;

                var writerType = asm.GetType("ModLogPlus.JsonLinesWriter");
                if (writerType == null) continue;

                var writeLine = writerType.GetMethod("WriteLine",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string) }, null);
                if (writeLine == null) continue;

                var json = $"{{\"ts\":\"{DateTime.Now:HH:mm:ss.fff}\"," +
                           $"\"mod\":\"sts2-spawn-cheat\"," +
                           $"\"level\":\"{level}\"," +
                           $"\"msg\":{jsonMessage}}}";

                writeLine.Invoke(null, new object[] { json });

                // 也写一份到独立错误文件
                var logDir = GetModLogPlusLogDir(asm);
                if (logDir != null)
                {
                    var errPath = Path.Combine(logDir, "spawn-cheat-init-error.txt");
                    File.WriteAllText(errPath,
                        $"ts={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\n" +
                        $"level={level}\n" +
                        $"msg={CardRewardPatch.SanitizePath(jsonMessage)}\n");
                }
                return;
            }
        }
        catch
        {
            // ModLogPlus 不可用 → 静默降级
        }
    }

    private static string? GetModLogPlusLogDir(Assembly modLogPlusAsm)
    {
        try
        {
            var pathHelper = modLogPlusAsm.GetType("ModLogPlus.PathHelper");
            if (pathHelper == null) return null;

            var resolveLogDir = pathHelper.GetMethod("ResolveLogDir",
                BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            return resolveLogDir?.Invoke(null, null) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>JSON 字符串转义（用于手动构建 JSON）</summary>
    private static string JsonEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        return "\"" + s.Replace("\\", "\\\\")
                      .Replace("\"", "\\\"")
                      .Replace("\r", "\\r")
                      .Replace("\n", "\\n")
                      .Replace("\t", "\\t") + "\"";
    }
}

/// <summary>NGame._Ready 的 Postfix 方法容器</summary>
internal static class NGameReadyMethods
{
    public static void OnReady()
    {
        GD.Print("[SpawnCheat] NGame._Ready detected. Creating overlay...");
        InputHandler.Attach();
    }
}
