using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace MapOddsTracker.Scripts;

[ModInitializer(ModConstants.ModInitMethod)]
public class Entry
{
    public static void Init()
    {
        var harmony = new Harmony(ModConstants.HarmonyId);
        harmony.PatchAll();
        // MapOverlay 是纯 C# 直接 new 创建的，不需要 Godot 脚本注册
        // 注册脚本类型会导致客户端和主机类型表不一致，引发同步问题
        ModLogger.Log("Initialized successfully!");
    }
}