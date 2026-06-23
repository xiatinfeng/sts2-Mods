using Godot;
using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;
using Sts2SpawnCheat.Core;

namespace Sts2SpawnCheat.Patches;

/// <summary>
/// PotionReward Populate Postfix — 队列消耗式替换药水。
/// </summary>
internal static class PotionRewardPatch
{
    private static readonly Type? T_PotionReward = AccessTools.TypeByName("MegaCrit.Sts2.Core.Rewards.PotionReward");
    private static readonly Type? T_PotionModel = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.PotionModel");
    private static readonly PropertyInfo? P_Potion = T_PotionReward?.GetProperty("Potion", BindingFlags.Public | BindingFlags.Instance);

    public static void Register(Harmony harmony)
    {
        if (T_PotionReward == null) { GD.PrintErr("[SpawnCheat] PotionReward not found"); return; }
        var populate = AccessTools.Method(T_PotionReward, "Populate", Type.EmptyTypes);
        if (populate == null) { GD.PrintErr("[SpawnCheat] PotionReward.Populate not found"); return; }
        harmony.Patch(populate, postfix: new HarmonyMethod(typeof(PotionRewardPatch), nameof(Postfix)));
        GD.Print("[SpawnCheat] PotionReward patch registered");
    }

    public static void Postfix(object __instance)
    {
        var potionId = CardSpawnService.ConsumeNextPotion();
        if (potionId == null) return;

        var target = ModelDb.AllPotions.FirstOrDefault(p => p.Id.Entry == potionId);
        if (target == null) return;
        if (P_Potion == null) return;

        var mutable = target.GetType().GetMethod("ToMutable", BindingFlags.Public | BindingFlags.Instance)?.Invoke(target, null);
        if (mutable == null) return;

        P_Potion.SetValue(__instance, mutable);
        CardRewardPatch.DiagLog($"PotionReward: replaced with [{potionId}]");
    }
}
