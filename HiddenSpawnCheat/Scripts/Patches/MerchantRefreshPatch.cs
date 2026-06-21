using Godot;
using HarmonyLib;
using System.Reflection;
using Sts2SpawnCheat.Core;

namespace Sts2SpawnCheat.Patches;

/// <summary>
/// 商店界面注入「🔄 刷新」按钮。
/// 
/// Hook: NMerchantInventory.Initialize(MerchantInventory, MerchantDialogueSet) Postfix
/// 参照 Jianbao233-RefreshShop v0.1.2 的方式：
/// - 通过 _cardRemovalNode 字段定位按钮位置
/// - 用 GuiInput 处理点击（而非 Button.Pressed）
/// </summary>
internal static class MerchantRefreshPatch
{
    private static readonly Type? T_MerchantInventory =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory");

    private const string ButtonName = "SpawnCheatShopRefreshBtn";

    public static void Register(Harmony harmony)
    {
        if (T_MerchantInventory == null)
        {
            GD.PrintErr("[SpawnCheat] NMerchantInventory type not found, shop refresh button skipped");
            return;
        }

        var initMethod = AccessTools.Method(T_MerchantInventory, "Initialize");
        if (initMethod == null)
        {
            GD.PrintErr("[SpawnCheat] NMerchantInventory.Initialize not found");
            return;
        }

        var postfix = AccessTools.Method(typeof(MerchantRefreshPatch), nameof(OnInitializePostfix));
        harmony.Patch(initMethod, postfix: new HarmonyMethod(postfix));
        GD.Print("[SpawnCheat] Shop refresh button patch registered (on Initialize)");
    }

    public static void OnInitializePostfix(object __instance)
    {
        if (__instance == null) return;

        try
        {
            // 找 NMerchantRoom 作为按钮的父级（离开商店时自动销毁）
            var invNode = __instance as Node;
            if (invNode == null) return;
            var merchantRoom = invNode.GetParent();
            if (merchantRoom == null) return;

            // 检查是否已有按钮
            if (merchantRoom.FindChild(ButtonName, recursive: true) != null) return;

            // 使用标准 Button（和卡牌刷新按钮一样的样式）
            var btn = new Button();
            btn.Name = ButtonName;
            btn.Text = "🔄 刷新商店";
            btn.CustomMinimumSize = new Vector2(140, 36);

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

            var hoverStyle = new StyleBoxFlat();
            hoverStyle.BgColor = new Color(0.25f, 0.2f, 0.1f, 0.95f);
            hoverStyle.BorderColor = new Color(0.8f, 0.7f, 0.2f);
            hoverStyle.SetBorderWidthAll(1);
            hoverStyle.CornerRadiusBottomLeft = 4;
            hoverStyle.CornerRadiusBottomRight = 4;
            hoverStyle.CornerRadiusTopLeft = 4;
            hoverStyle.CornerRadiusTopRight = 4;
            btn.AddThemeStyleboxOverride("hover", hoverStyle);

            // 用 CanvasLayer 确保按钮渲染在 UI 顶层
            var layer = new CanvasLayer();
            layer.Name = ButtonName + "Layer";
            layer.Layer = 200;

            // 底部居中定位
            btn.AnchorLeft = 0.5f;
            btn.AnchorRight = 0.5f;
            btn.AnchorTop = 1f;
            btn.AnchorBottom = 1f;
            btn.OffsetLeft = -70;
            btn.OffsetRight = 70;
            btn.OffsetTop = -50;
            btn.OffsetBottom = -20;

            // 点击处理
            btn.Pressed += () =>
            {
                GD.Print("[SpawnCheat] Shop refresh button clicked");
                ShopSpawnService.RefreshShopOnNode(__instance);
            };

            layer.AddChild(btn);
            merchantRoom.AddChild(layer);
            GD.Print("[SpawnCheat] Shop refresh button injected under " + merchantRoom.Name);
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[SpawnCheat] Shop refresh button injection failed: {ex.Message}");
        }
    }
}
