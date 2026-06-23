using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sts2SpawnCheat.Core;

namespace Sts2SpawnCheat.Patches;

/// <summary>
/// 屏蔽作弊模组检测：从联机 Mod 列表中移除本模组，使客机无法察觉。
/// 参考煎包 NoClientCheats.ModListFilterPatch。
/// </summary>
[HarmonyPatch]
internal static class ModListFilterPatch
{
    private const string ModIdPrefix = "sts2-spawn-cheat";

    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("MegaCrit.Sts2.Core.Modding.ModManager")
            ?? AccessTools.TypeByName("ModManager");
        if (t == null) return null;
        var m = t.GetMethod("GetGameplayRelevantModNameList", BindingFlags.Public | BindingFlags.Static)
            ?? t.GetMethod("GetModNameList", BindingFlags.Public | BindingFlags.Static);
        return m;
    }

    static void Postfix(ref List<string> __result)
    {
        if (!ModConfigIntegration.HideFromModList) return;
        if (__result == null) return;
        __result.RemoveAll(s => s != null && s == ModIdPrefix);
    }
}
