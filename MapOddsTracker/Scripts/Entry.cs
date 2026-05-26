using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace MapOddsTracker.Scripts;

[ModInitializer("Init")]
public class Entry
{
    public static void Init()
    {
        var harmony = new Harmony("sts2.user.mapoddstracker");
        harmony.PatchAll();
        // 注意：不调用 ScriptManagerBridge.LookupScriptsInAssembly
        // 因为 MapOverlay 是纯 C# 直接 new 创建的，不需要 Godot 脚本注册
        // 注册脚本类型会导致客户端和主机类型表不一致，引发同步问题
        ModLogger.Log("Initialized successfully!");
    }
}