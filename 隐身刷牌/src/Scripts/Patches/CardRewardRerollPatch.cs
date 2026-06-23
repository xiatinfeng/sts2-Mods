using Godot;
using HarmonyLib;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sts2SpawnCheat.Core;

namespace Sts2SpawnCheat.Patches;

/// <summary>
/// 卡牌奖励界面注入「🔄 刷新」按钮。
/// 
/// Hook: NCardRewardSelectionScreen._Ready() Postfix
/// 在界面底部加入刷新按钮，点击后调用 CardReward.Reroll()，
/// 重新生成卡牌并从 CardSpawnService.SelectedCardIdsForReward 注入已标记卡。
/// 
/// 按钮防重复：用静态 HashSet 记录已注入的屏幕实例。
/// </summary>
internal static class CardRewardRerollPatch
{
    // ─── 类型反射缓存 ───────────────────────────────
    private static readonly Type? T_Screen =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen");
    private static readonly Type? T_CardReward =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Rewards.CardReward");

    private static readonly FieldInfo? F__options =
        T_Screen?.GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? F__cards =
        T_CardReward?.GetField("_cards", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? F__currentlyShownScreen =
        T_CardReward?.GetField("_currentlyShownScreen", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? F_CanReroll =
        T_CardReward?.GetField("CanReroll", BindingFlags.Public | BindingFlags.Instance);

    /// <summary>当前活跃的 CardReward 实例（Populate 时记录）</summary>
    private static object? _activeCardReward;

    /// <summary>由 PopulatePatch 调用，记录当前 CardReward 实例</summary>
    public static void SetActiveCardReward(object instance)
    {
        _activeCardReward = instance;
    }

    /// <summary>已注入按钮的屏幕实例（防重复）</summary>
    private static readonly HashSet<object> _injectedScreens = new();

    /// <summary>Button 名称常量（用于防重复检查 + 查找）</summary>
    private const string ButtonName = "SpawnCheatRerollBtn";

    public static void Register(Harmony harmony)
    {
        if (T_Screen == null)
        {
            GD.PrintErr("[SpawnCheat] NCardRewardSelectionScreen not found, reroll button skipped");
            return;
        }

        var ready = AccessTools.Method(T_Screen, "_Ready");
        if (ready == null)
        {
            GD.PrintErr("[SpawnCheat] NCardRewardSelectionScreen._Ready not found, reroll button skipped");
            return;
        }

        var postfix = AccessTools.Method(typeof(CardRewardRerollPatch), nameof(OnReadyPostfix));
        harmony.Patch(ready, postfix: new HarmonyMethod(postfix));
        GD.Print("[SpawnCheat] Reroll button patch registered");
    }

    public static void OnReadyPostfix(object __instance)
    {
        if (__instance == null) return;

        // 防重复注入
        if (!_injectedScreens.Add(__instance)) return;

        try
        {
            // 找到屏幕的 _ui 节点作为父容器
            var uiProp = __instance.GetType().GetProperty("_ui",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var ui = (uiProp?.GetValue(__instance) as Control) ?? (__instance as Control);
            if (ui == null) return;

            // 查找或创建按钮容器（底部）
            var btn = new Button();
            btn.Name = ButtonName;
            btn.Text = "🔄 刷新";
            btn.CustomMinimumSize = new Vector2(100, 32);

            // 金色样式
            btn.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.3f));
            var normalStyle = new StyleBoxFlat();
            normalStyle.BgColor = new Color(0.15f, 0.12f, 0.08f, 0.9f);
            normalStyle.BorderColor = new Color(0.6f, 0.5f, 0.15f);
            normalStyle.SetBorderWidthAll(1);
            normalStyle.CornerRadiusBottomLeft = 4;
            normalStyle.CornerRadiusBottomRight = 4;
            normalStyle.CornerRadiusTopLeft = 4;
            normalStyle.CornerRadiusTopRight = 4;
            btn.AddThemeStyleboxOverride("normal", normalStyle);

            // Hover 高亮
            var hoverStyle = new StyleBoxFlat();
            hoverStyle.BgColor = new Color(0.25f, 0.2f, 0.1f, 0.95f);
            hoverStyle.BorderColor = new Color(0.8f, 0.7f, 0.2f);
            hoverStyle.SetBorderWidthAll(1);
            hoverStyle.CornerRadiusBottomLeft = 4;
            hoverStyle.CornerRadiusBottomRight = 4;
            hoverStyle.CornerRadiusTopLeft = 4;
            hoverStyle.CornerRadiusTopRight = 4;
            btn.AddThemeStyleboxOverride("hover", hoverStyle);

            // 使用绝对定位：右下角、但上移避免被截断
            // 先设锚点为右下角
            btn.AnchorLeft = 1f;
            btn.AnchorRight = 1f;
            btn.AnchorTop = 1f;
            btn.AnchorBottom = 1f;
            // 然后从右下角偏移：左移 120px，上移 120px
            btn.OffsetLeft = -120 - btn.CustomMinimumSize.X;
            btn.OffsetRight = -120;
            btn.OffsetTop = -120 - btn.CustomMinimumSize.Y;
            btn.OffsetBottom = -120;

            // 点击事件：找 CardReward → 调 Reroll()
            btn.Pressed += () =>
            {
                try
                {
                    var cardReward = _activeCardReward;
                    if (cardReward == null)
                    {
                        GD.PrintErr("[SpawnCheat] Reroll: CardReward not found");
                        return;
                    }

                    GD.Print("[SpawnCheat] Reroll: silent refresh (no history)...");
                    var cards = F__cards?.GetValue(cardReward);
                    if (cards is IList list) list.Clear();
                    if (F_CanReroll != null) F_CanReroll.SetValue(cardReward, false);

                    var populate = AccessTools.Method(T_CardReward, "Populate", Type.EmptyTypes);
                    if (populate != null)
                        populate.Invoke(cardReward, null);
                }
                catch (System.Exception ex)
                {
                    GD.PrintErr($"[SpawnCheat] Reroll failed: {ex.Message}");
                }
            };

            ui.AddChild(btn);
            GD.Print("[SpawnCheat] Reroll button injected");
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[SpawnCheat] Reroll button injection failed: {ex.Message}");
        }
    }

    /// <summary>通过静态缓存找到与屏幕关联的 CardReward 实例</summary>
    private static object? FindCardReward(object screen)
    {
        // PopulatePatch 已记录当前活跃的 CardReward 实例
        return _activeCardReward;
    }
}
