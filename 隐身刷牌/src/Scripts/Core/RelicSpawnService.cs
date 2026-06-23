using System.Linq;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2SpawnCheat.Core;

/// <summary>
/// 遗物作弊核心服务。
/// 
/// 参考自 WRXinYue KitLib RelicActions 和 ataraxia7899 DevMode MiscTab：
/// - ModelDb.AllRelics 枚举所有遗物
/// - RelicCmd.Obtain(relic.ToMutable(), player, -1) 直接获得遗物
/// - relic.ToMutable() 创建可变副本（不能直接传原型）
/// </summary>
public static class RelicSpawnService
{
    /// <summary>按 ID 查找遗物原型</summary>
    public static RelicModel? FindRelic(string relicId)
    {
        if (string.IsNullOrEmpty(relicId)) return null;
        return ModelDb.AllRelics
            .FirstOrDefault(r => ((AbstractModel)r).Id.Entry == relicId);
    }

    /// <summary>直接获得遗物（即时生效）</summary>
    public static void ObtainRelic(RelicModel relic)
    {
        if (!RunManager.Instance.IsInProgress) return;

        Player? player = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
        if (player == null) return;

        var task = RelicCmd.Obtain(relic.ToMutable(), player, -1);
        TaskHelper.RunSafely(task);
    }
}
