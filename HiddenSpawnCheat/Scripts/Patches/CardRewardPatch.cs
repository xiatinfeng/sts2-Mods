using Godot;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sts2SpawnCheat.Core;

namespace Sts2SpawnCheat.Patches;

/// <summary>
/// CardReward 补丁 — 队列消耗式顶替。
/// 
/// CardSpawnService.ReplacementQueue 按顺序存入标记卡 ID。
/// 每次 Populate 时消费队列头的一张卡，替换 _cards 第 1 张。
/// CardChoices 始终 3 条原生记录，零历史清洗。
/// </summary>
internal static class CardRewardPatch
{
    private static readonly Type? T_CardReward = AccessTools.TypeByName("MegaCrit.Sts2.Core.Rewards.CardReward");
    private static readonly Type? T_CardModel = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.CardModel");
    private static readonly Type? T_Player = AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.Players.Player");
    private static readonly Type? T_CardCreationResult = AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult");

    private static readonly FieldInfo? F__cards = T_CardReward?.GetField("_cards", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? F__currentlyShownScreen = T_CardReward?.GetField("_currentlyShownScreen", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? P_Player = T_CardReward?.GetProperty("Player", BindingFlags.Public | BindingFlags.Instance);

    // ═══════════════════════════════════════════════════
    //  调试日志（默认关闭，发布前请保持 false）
    // ═══════════════════════════════════════════════════

    /// <summary>调试日志总开关。发布前保持 false。</summary>
    public static bool DebugLoggingEnabled { get; set; } = false;

    public static readonly string DebugLogPath = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "Godot", "app_userdata", "Slay the Spire 2", "spawn-cheat-debug.log");

    public static void DiagLog(string msg)
    {
        if (!DebugLoggingEnabled) return;
        try { System.IO.File.AppendAllText(DebugLogPath, $"[Diag] {msg}\n"); }
        catch { }
    }

    /// <summary>过滤堆栈中的本机路径，防止泄露用户名/目录结构</summary>
    public static string SanitizePath(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        // 替换 C:\Users\用户名 等模式
        var userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
            return input.Replace(userProfile, "%USERPROFILE%");
        return input;
    }

    public static void SafeLog(string prefix, string message)
    {
        if (!DebugLoggingEnabled) return;
        DiagLog($"{prefix}: {SanitizePath(message)}");
    }

    // ═══════════════════════════════════════════════════
    //  Populate Postfix — 队列消耗式顶替
    // ═══════════════════════════════════════════════════

    internal static class PopulatePatch
    {
        // 同一实例的奖励屏只消耗一次队列（防刷新重复消耗）
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<object, string?> _usedCards = new();

        public static void Postfix(object __instance)
        {
            // 记录活跃 CardReward 实例（供刷新按钮使用）
            CardRewardRerollPatch.SetActiveCardReward(__instance);

            // 如果开启「刷新不消耗队列」，检查是否已为此实例消耗过
            if (CardSpawnService.ReuseOnReroll)
            {
                if (_usedCards.TryGetValue(__instance, out var cachedCard) && cachedCard != null)
                {
                    ReplaceCard(__instance, cachedCard);
                    return;
                }
            }

            // 取队列头（消费掉）
            var cardId = CardSpawnService.ConsumeNextReplacement();
            if (cardId == null) return;
            if (CardSpawnService.ReuseOnReroll) _usedCards[__instance] = cardId;

            ReplaceCard(__instance, cardId);
        }

        private static void ReplaceCard(object __instance, string cardId)
        {

            var cardsRaw = F__cards?.GetValue(__instance);
            if (cardsRaw == null) return;
            var cards = cardsRaw as IList;
            if (cards == null || cards.Count == 0) return;

            var player = P_Player?.GetValue(__instance);
            if (player == null) return;

            var runManager = AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager")
                ?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (runManager == null) return;
            var getState = runManager.GetType().GetMethod("DebugOnlyGetState",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            var state = getState?.Invoke(runManager, null);
            if (state == null || T_CardModel == null || T_CardCreationResult == null) return;

            var createCard = state.GetType().GetMethod("CreateCard",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { T_CardModel, T_Player }, null);
            if (createCard == null) return;

            var cardModel = CardSpawnService.FindCard(cardId);
            if (cardModel == null) return;

            var createdCard = createCard.Invoke(state, new[] { cardModel, player });
            if (createdCard == null) return;

            // ─── 升级 ───
            if (CardSpawnService.MarkedCardsUpgraded)
            {
                try
                {
                    var upg = T_CardModel?.GetMethod("UpgradeInternal", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    upg?.Invoke(createdCard, null);
                    var fin = T_CardModel?.GetMethod("FinalizeUpgradeInternal", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    fin?.Invoke(createdCard, null);
                }
                catch (Exception ex) { DiagLog($"Upgrade failed: {ex.Message}"); }
            }

            // ─── 附魔 ───
            var eid = CardSpawnService.SelectedEnchantmentId;
            if (eid != null)
            {
                try
                {
                    var eType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Enchantments." + eid);
                    if (eType != null && T_CardModel != null)
                    {
                        // ModelDb.Enchantment<T>() → 拿 canonical
                        var modelDb = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.ModelDb");
                        var enchantGeneric = modelDb?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .FirstOrDefault(m => m.Name == "Enchantment" && m.IsGenericMethod && m.GetParameters().Length == 0);
                        if (enchantGeneric != null)
                        {
                            var enchantSpecific = enchantGeneric.MakeGenericMethod(eType);
                            var canonical = enchantSpecific?.Invoke(null, null);
                            if (canonical != null)
                            {
                                var toMut = eType.GetMethod("ToMutable", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                                var eMut = toMut?.Invoke(canonical, null);
                                if (eMut != null)
                                {
                                    var eInt = T_CardModel.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                        .FirstOrDefault(m => m.Name == "EnchantInternal" && m.GetParameters().Length == 2);
                                    if (eInt != null)
                                        eInt.Invoke(createdCard, new[] { eMut, 1m });
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { DiagLog($"Enchant failed: {ex.Message} | Inner: {ex.InnerException?.Message}"); }
            }

            var result = Activator.CreateInstance(T_CardCreationResult, createdCard);
            if (result == null) return;

            // 替换 _cards 第 0 张
            cards[0] = result;
            // 延迟刷新视觉（避免全屏重建导致的遮罩）
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree != null)
            {
                var timer = new Godot.Timer();
                timer.WaitTime = 0.05f;
                timer.OneShot = true;
                timer.Timeout += () => RefreshUi(__instance, cardsRaw);
                tree.Root.AddChild(timer);
                timer.Start();
            }
            DiagLog($"Populate: replaced card 0 with [{cardId}]");
        }

        private static void RefreshSingleCard(object instance, object newResult)
    {
        // 找屏幕的 _options（卡牌节点列表）
        var screen = F__currentlyShownScreen?.GetValue(instance);
        if (screen == null) return;
        var optionsField = screen.GetType().GetField("_options",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var options = optionsField?.GetValue(screen) as IList;
        if (options == null || options.Count == 0) return;

        // 更新第一个卡牌节点的 CardCreationResult
        var firstOption = options[0];
        var resultField = firstOption?.GetType().GetField("_result",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (resultField != null) resultField.SetValue(firstOption, newResult);

        // 调用 UpdateVisuals 刷新显示
        var updateMethod = firstOption?.GetType().GetMethod("UpdateVisuals",
            BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
            ?? firstOption?.GetType().GetMethod("RefreshDisplay",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        updateMethod?.Invoke(firstOption, null);
    }

    private static void RefreshUi(object instance, object cardsRaw)
        {
            var screen = F__currentlyShownScreen?.GetValue(instance);
            if (screen == null || T_CardReward == null || T_CardCreationResult == null) return;

            var tAlt = AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.CardRewardAlternatives.CardRewardAlternative");
            if (tAlt == null) return;

            var mRefresh = AccessTools.Method(screen.GetType(), "RefreshOptions", new[]
            {
                typeof(IReadOnlyList<>).MakeGenericType(T_CardCreationResult),
                typeof(IReadOnlyList<>).MakeGenericType(tAlt)
            });
            if (mRefresh == null) return;

            var mGenerate = AccessTools.Method(tAlt, "Generate", new[] { T_CardReward });
            var extraOptions = mGenerate?.Invoke(null, new[] { instance });
            mRefresh.Invoke(screen, new[] { cardsRaw, extraOptions });
        }
    }
}
