using Godot;
using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;
using Sts2SpawnCheat.Core;

namespace Sts2SpawnCheat.Patches;

/// <summary>
/// RelicReward Populate Postfix — 队列消耗式替换遗物。
/// 标记的遗物按顺序每次奖励消耗一个。
/// </summary>
internal static class RelicRewardPatch
{
    private static readonly Type? T_RelicReward = AccessTools.TypeByName("MegaCrit.Sts2.Core.Rewards.RelicReward");
    private static readonly Type? T_RelicModel = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.RelicModel");
    private static readonly FieldInfo? F__relic = T_RelicReward?.GetField("_relic", BindingFlags.Instance | BindingFlags.NonPublic);

    public static void Register(Harmony harmony)
    {
        if (T_RelicReward == null) { GD.PrintErr("[SpawnCheat] RelicReward not found"); return; }
        var populate = AccessTools.Method(T_RelicReward, "Populate", Type.EmptyTypes);
        if (populate == null) { GD.PrintErr("[SpawnCheat] RelicReward.Populate not found"); return; }
        harmony.Patch(populate, postfix: new HarmonyMethod(typeof(RelicRewardPatch), nameof(Postfix)));
        GD.Print("[SpawnCheat] RelicReward patch registered");
    }

    public static void Postfix(object __instance)
    {
        var relicId = CardSpawnService.ConsumeNextRelic();
        if (relicId == null) return;

        var target = ModelDb.AllRelics.FirstOrDefault(r => r.Id.Entry == relicId);
        if (target == null) return;
        if (F__relic == null) return;

        var mutable = target.GetType().GetMethod("ToMutable", BindingFlags.Public | BindingFlags.Instance)?.Invoke(target, null);
        if (mutable == null) return;

        F__relic.SetValue(__instance, mutable);
        CardRewardPatch.DiagLog($"RelicReward: replaced with [{relicId}]");
    }
}
