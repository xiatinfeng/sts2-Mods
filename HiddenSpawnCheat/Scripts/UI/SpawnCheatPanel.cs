using Godot;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Runs;
using Sts2SpawnCheat.Core;
using Sts2SpawnCheat.Core;
using Sts2SpawnCheat.Patches;

namespace Sts2SpawnCheat.UI;

/// <summary>
/// 作弊主面板。F5 唤出/隐藏。
/// CanvasLayer (Layer=128) + Timer 轮询检测 F5。
/// 
/// 参考自 ataraxia7899 DevMode 的 DevModeOverlay 模式：
/// - 纯 C# 构建 Godot UI，不依赖 .tscn
/// - Timer 替代 _Process 轮询（外部 DLL 兼容性更好）
/// - 搜索+按钮列表模式
/// </summary>
public partial class SpawnCheatPanel : CanvasLayer
{
    private static SpawnCheatPanel? _instance;
    private Control? _rootPanel;
    private Control? _tabContentArea; // 容器，非 TabContainer
    private HBoxContainer? _tabBar;
    private Control? _titleBar;
    private bool _isVisible;
    private Label? _statusLabel;
    private static bool _f5WasPressed;
    private static Key _toggleKey = Key.F5;

    public static Key ToggleKey
    {
        get => _toggleKey;
        set
        {
            _toggleKey = value;
            CardRewardPatch.DiagLog($"ToggleKey updated to {value}");
            // 同步面板顶栏的快捷键提示
            if (_instance?._titleBar != null)
            {
                var hint = _instance._titleBar.GetNodeOrNull<Label>("Hint");
                if (hint != null)
                    hint.Text = $"[{value}]";
            }
            // 同步底部状态栏
            if (_instance?._statusLabel != null)
                _instance._statusLabel.Text = string.Format(I18n.T("status_bar"), value);
        }
    }
    private bool _dragging;
    private Vector2 _dragOffset;
    private CardTabContent? _cardTab;
    private RelicTabContent? _relicTab;
    private PotionTabContent? _potionTab;
    private ResourceTabContent? _resourceTab;
    private FavoriteTabContent? _favoriteTab;
    private Panel? _queueTab;
    private VBoxContainer? _queueTabContent;
    private int _activeTabIdx;

    /// <summary>在 NGame._Ready 之后调用，创建面板并挂载到场景树</summary>
    public static void CreateAndAttach()
    {
        if (_instance != null) return;

        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null) return;

        try
        {
            _instance = new SpawnCheatPanel();
            _instance._BuildAll(tree);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpawnCheat] Panel build error: {ex}");
        }
    }

    /// <summary>销毁面板实例</summary>
    public static void Destroy()
    {
        _instance?.QueueFree();
        _instance = null;
    }

    private void _BuildAll(SceneTree tree)
    {
        Layer = 128;
        Name = "SpawnCheatCanvasLayer";

        _BuildUi();
        _isVisible = false;
        _rootPanel!.Visible = false;

        // 场景切换可能重置 Visible → 每帧守卫
        var guardTimer = new Godot.Timer();
        guardTimer.WaitTime = 0.1f;
        guardTimer.Autostart = true;
        guardTimer.Timeout += () =>
        {
            if (_rootPanel != null && _rootPanel.Visible != _isVisible)
                Sts2SpawnCheat.Patches.CardRewardPatch.DiagLog($"Visible desync: isVisible={_isVisible} actual={_rootPanel.Visible} — correcting");
            if (_rootPanel != null && !_isVisible) _rootPanel.Visible = false;
            // 队列 Tab 脏刷新（仅当队列被消耗时）
            if (_activeTabIdx == 5 && CardSpawnService.QueueDirty)
            {
                CardSpawnService.QueueDirty = false;
                _RefreshQueueTab();
            }
        };
        AddChild(guardTimer);

        Sts2SpawnCheat.Patches.CardRewardPatch.DiagLog($"Panel created, rootPanel.Visible={_rootPanel?.Visible}");

        // Timer 轮询检测 F5（替代 _Process，外部 DLL 兼容）
        var timer = new Godot.Timer();
        timer.WaitTime = 0.016f; // ~60 FPS
        timer.Autostart = true;
        timer.Timeout += _CheckInput;
        AddChild(timer);

        // ModConfig 软依赖：延迟 1.5 秒注册
        tree.CreateTimer(1.5f).Timeout += () =>
        {
            CardRewardPatch.DiagLog("ModConfig timer fired");
            ModConfigIntegration.TryRegister(() =>
            {
                _isVisible = !_isVisible;
                if (_rootPanel != null)
                    _rootPanel.Visible = _isVisible;
                if (_isVisible)
                {
                    _cardTab?.Refresh();
                    _relicTab?.Refresh();
                    _potionTab?.Refresh();
                }
            });
        };

        // 挂载到 NGame.Instance 或 Root
        if (MegaCrit.Sts2.Core.Nodes.NGame.Instance != null)
            MegaCrit.Sts2.Core.Nodes.NGame.Instance.AddChild(this, forceReadableName: true);
        else
            tree.Root.AddChild(this, forceReadableName: true);
    }

    private void _CheckInput()
    {
        if (Input.IsPhysicalKeyPressed(_toggleKey))
        {
            if (!_f5WasPressed)
            {
                _f5WasPressed = true;
                _isVisible = !_isVisible;
                if (_rootPanel != null)
                    _rootPanel.Visible = _isVisible;

                if (_isVisible)
                {
                    _cardTab?.Refresh();
                    _relicTab?.Refresh();
                    _potionTab?.Refresh();
                }
            }
        }
        else
        {
            _f5WasPressed = false;
        }
    }

    private void _BuildUi()
    {
        var scene = ResourceLoader.Load<PackedScene>("res://Scripts/UI/SpawnCheatPanel.tscn");
        if (scene != null)
        {
            try
            {
                System.IO.File.AppendAllText(CardRewardPatch.DebugLogPath, "[BUILD] Using scene path\n");
                _BuildUiFromScene(scene);
                return;
            }
            catch (Exception ex)
            {
                System.IO.File.AppendAllText(CardRewardPatch.DebugLogPath, $"[BUILD] Scene failed: {ex.Message}\n");
                GD.PrintErr($"[SpawnCheat] .tscn loaded but init failed: {ex.Message}, falling back to code");
            }
        }
        else
        {
            GD.Print("[SpawnCheat] .tscn not found at res://Scripts/UI/SpawnCheatPanel.tscn, building UI in code");
        }
        System.IO.File.AppendAllText(CardRewardPatch.DebugLogPath, "[BUILD] Using code path\n");
        _BuildUiFromCode();
    }

    private void _BuildUiFromScene(PackedScene scene)
    {
        _rootPanel = scene.Instantiate<Control>();
        _rootPanel.Name = "RootPanel";
        AddChild(_rootPanel);

        // 从场景中找子节点
        var bgPanel = _rootPanel.GetNode<Panel>("BgPanel");
        _tabContentArea = _rootPanel.GetNode<Panel>("Margin/VBox/TabContentArea");
        _tabBar = _rootPanel.GetNode<HBoxContainer>("Margin/VBox/TabBar");
        _titleBar = _rootPanel.GetNode<HBoxContainer>("Margin/VBox/TitleBar");

        // 背景样式
        var bg = new StyleBoxFlat();
        bg.BgColor = new Color(0.06f, 0.06f, 0.09f, 0.98f);
        bg.BorderColor = new Color(0.55f, 0.45f, 0.2f, 0.8f);
        bg.SetBorderWidthAll(2);
        bg.CornerRadiusBottomLeft = 6;
        bg.CornerRadiusBottomRight = 6;
        bg.CornerRadiusTopLeft = 6;
        bg.CornerRadiusTopRight = 6;
        bg.ContentMarginLeft = 4;
        bg.ContentMarginRight = 4;
        bg.ContentMarginTop = 4;
        bg.ContentMarginBottom = 6;
        bgPanel.AddThemeStyleboxOverride("panel", bg);

        // 标题栏子节点
        _BuildTitleBarChildren();

        // Tab 按钮
        _BuildTabBar();

        // Tab 内容
        BuildTabContents();

        _activeTabIdx = 0;

        // 拖拽支持
        _rootPanel.GuiInput += OnRootPanelInput;
    }

    private void _BuildUiFromCode()
    {
        // 根节点用 Control，居中窗口
        _rootPanel = new Control();
        _rootPanel.SetSize(new Vector2(840, 600));
        _rootPanel.CustomMinimumSize = new Vector2(760, 580);
        _rootPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        AddChild(_rootPanel);

        // 画背景
        var bg = new StyleBoxFlat();
        bg.BgColor = new Color(0.06f, 0.06f, 0.09f, 0.98f);
        bg.BorderColor = new Color(0.55f, 0.45f, 0.2f, 0.8f);
        bg.SetBorderWidthAll(2);
        bg.CornerRadiusBottomLeft = 6;
        bg.CornerRadiusBottomRight = 6;
        bg.CornerRadiusTopLeft = 6;
        bg.CornerRadiusTopRight = 6;
        bg.ContentMarginLeft = 4;
        bg.ContentMarginRight = 4;
        bg.ContentMarginTop = 4;
        bg.ContentMarginBottom = 6;

        var bgPanel = new Panel();
        bgPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        bgPanel.AddThemeStyleboxOverride("panel", bg);
        bgPanel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _rootPanel.AddChild(bgPanel);

        // 内容边距容器
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 4);
        margin.AddThemeConstantOverride("margin_right", 4);
        margin.AddThemeConstantOverride("margin_top", 4);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        _rootPanel.AddChild(margin);

        // 外层 VBox
        var vbox = new VBoxContainer();
        vbox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        vbox.AddThemeConstantOverride("separation", 4);
        margin.AddChild(vbox);

        // 标题栏
        _titleBar = new HBoxContainer();
        _titleBar.CustomMinimumSize = new Vector2(0, 32);
        vbox.AddChild(_titleBar);
        _BuildTitleBarChildren();

        // Tab 栏
        _tabBar = new HBoxContainer();
        _tabBar.CustomMinimumSize = new Vector2(0, 24);
        vbox.AddChild(_tabBar);
        _BuildTabBar();

        // Tab 内容区域
        _tabContentArea = new Panel();
        _tabContentArea.MouseFilter = Control.MouseFilterEnum.Ignore;
        _tabContentArea.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _tabContentArea.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _tabContentArea.ClipContents = true;
        vbox.AddChild(_tabContentArea);

        // Tab 内容
        BuildTabContents();

        _activeTabIdx = 0;

        // 底部状态栏
        var statusLabel = new Label();
        statusLabel.Text = string.Format(I18n.T("status_bar"), _toggleKey);
        statusLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.9f, 0.7f));
        statusLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _statusLabel = statusLabel;
        vbox.AddChild(statusLabel);

        // 拖拽支持
        _rootPanel.GuiInput += OnRootPanelInput;
    }

    private void BuildTabContents()
    {
        // 清空场景中的占位子节点（如果有）
        foreach (var child in _tabContentArea.GetChildren())
            _tabContentArea.RemoveChild(child);

        _cardTab = new CardTabContent();
        _cardTab.Name = "CardTab";
        _cardTab.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _cardTab.Init();
        _tabContentArea.AddChild(_cardTab);

        _relicTab = new RelicTabContent();
        _relicTab.Name = "RelicTab";
        _relicTab.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _relicTab.Init();
        _relicTab.Visible = false;
        _tabContentArea.AddChild(_relicTab);

        _potionTab = new PotionTabContent();
        _potionTab.Name = "PotionTab";
        _potionTab.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _potionTab.Init();
        _potionTab.Visible = false;
        _tabContentArea.AddChild(_potionTab);

        _resourceTab = new ResourceTabContent();
        _resourceTab.Name = "ResourceTab";
        _resourceTab.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _resourceTab.Init();
        _resourceTab.Visible = false;
        _tabContentArea.AddChild(_resourceTab);

        _favoriteTab = new FavoriteTabContent();
        _favoriteTab.Name = "FavoriteTab";
        _favoriteTab.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _favoriteTab.Init();
        _favoriteTab.Visible = false;
        _tabContentArea.AddChild(_favoriteTab);

        // 队列 Tab
        _queueTab = new Panel();
        _queueTab.Name = "QueueTab";
        _queueTab.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _queueTab.Visible = false;
        _queueTabContent = new VBoxContainer();
        _queueTabContent.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _queueTab.AddChild(_queueTabContent);
        _tabContentArea.AddChild(_queueTab);
    }

    private void _BuildTabBar()
    {
        var tabNames = new[] { I18n.T("tab_cards"), I18n.T("tab_relics"), I18n.T("tab_potions"), I18n.T("tab_resources"), I18n.T("tab_favorites"), I18n.T("tab_queue") };

        for (int i = 0; i < tabNames.Length; i++)
        {
            var idx = i;
            var btn = _tabBar!.GetNodeOrNull<Button>($"TabBtn{i}");
            if (btn == null)
            {
                btn = new Button();
                btn.Text = tabNames[i];
                btn.CustomMinimumSize = new Vector2(60, 22);
                // 如果 _tabBar 没有子节点（代码路径），用容器布局
                if (_tabBar!.GetChildCount() == 0)
                {
                    btn.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
                }
                else
                {
                    // 场景路径：已有按钮用绝对定位，新按钮也要对齐
                    btn.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                    btn.OffsetLeft = 270;
                    btn.OffsetTop = 38;
                    btn.OffsetRight = 330;
                    btn.OffsetBottom = 69;
                }
                _tabBar.AddChild(btn);
            }
            btn.AddThemeFontSizeOverride("font_size", 12);
            btn.AddThemeColorOverride("font_color",
                i == 0 ? new Color(1f, 0.88f, 0.35f) : new Color(0.6f, 0.6f, 0.7f));
            var capturedIdx = i;
            btn.Pressed += () => {
                System.IO.File.AppendAllText(CardRewardPatch.DebugLogPath,
                    $"[{System.DateTime.Now:HH:mm:ss}] TabBtn{capturedIdx} clicked\n");
                _SwitchTab(capturedIdx);
            };
        }

        // 颜色条背景
        var tabBgPanel = _tabBar!.GetNodeOrNull<Panel>("TabBtnBg");
        if (tabBgPanel == null)
        {
            tabBgPanel = new Panel();
            tabBgPanel.MouseFilter = Control.MouseFilterEnum.Ignore;
            _tabBar.AddChild(tabBgPanel);
        }
        var tabBg = new StyleBoxFlat();
        tabBg.BgColor = new Color(0.1f, 0.1f, 0.14f, 0.9f);
        tabBg.BorderColor = new Color(0.25f, 0.2f, 0.1f);
        tabBg.SetBorderWidthAll(1);
        tabBg.CornerRadiusTopLeft = 4;
        tabBg.CornerRadiusTopRight = 4;
        tabBgPanel.AddThemeStyleboxOverride("panel", tabBg);
        tabBgPanel.Owner = _tabBar;
        // 确保背景在底层
        _tabBar.MoveChild(tabBgPanel, 0);
    }

    private void _SwitchTab(int idx)
    {
        _activeTabIdx = idx;
        var tabs = new Control?[] { _cardTab, _relicTab, _potionTab, _resourceTab, _favoriteTab, _queueTab };

        // ⚠️ Panel 不是 Container，切换可见性不会重算子节点
        // 解决：删掉全部子节点，只重新添加当前 active 的 tab
        // 新添加的子节点会触发 EnterTree → 初始布局计算，尺寸正确
        foreach (var child in _tabContentArea!.GetChildren())
            _tabContentArea.RemoveChild(child);

        var tab = tabs[idx];
        if (tab != null)
        {
            tab.Visible = true;
            tab.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _tabContentArea.AddChild(tab);
        }

        // 更新 tab 按钮颜色（按类型查找，跳过 TabBtnBg 等非 Button 子节点）
        if (_tabBar != null)
        {
            int btnIdx = 0;
            foreach (Node child in _tabBar.GetChildren())
            {
                if (child is Button btn && btnIdx < 6)
                {
                    btn.AddThemeColorOverride("font_color",
                        btnIdx == idx ? new Color(1f, 0.88f, 0.35f) : new Color(0.6f, 0.6f, 0.7f));
                    btnIdx++;
                }
            }
        }

        if (idx == 0) _cardTab?.Refresh();
        else if (idx == 1) _relicTab?.Refresh();
        else if (idx == 2) _potionTab?.Refresh();
        else if (idx == 3) _resourceTab?.Refresh();
        else if (idx == 5) _RefreshQueueTab();
        else if (idx == 4) { _favoriteTab?.Refresh(); System.IO.File.AppendAllText(CardRewardPatch.DebugLogPath, $"[{System.DateTime.Now:HH:mm:ss}] SwitchTab to 4 (收藏)\n"); }
    }

    private void _BuildTitleBarChildren()
    {
        // 找场景中的子节点，没有则创建（支持代码回退）
        var icon = _titleBar.GetNodeOrNull<Label>("Icon");
        if (icon == null) { icon = new Label(); icon.Text = "⚡"; icon.CustomMinimumSize = new Vector2(24, 0); _titleBar.AddChild(icon); }
        icon.AddThemeFontSizeOverride("font_size", 16);

        var title = _titleBar.GetNodeOrNull<Label>("Title");
        if (title == null) { title = new Label(); title.Text = "Spawn Cheat"; title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; _titleBar.AddChild(title); }
        title.AddThemeColorOverride("font_color", new Color(1f, 0.88f, 0.35f));
        title.AddThemeFontSizeOverride("font_size", 14);

        var hint = _titleBar.GetNodeOrNull<Label>("Hint");
        if (hint == null) { hint = new Label(); hint.Name = "Hint"; hint.Text = "[F5]"; hint.CustomMinimumSize = new Vector2(30, 0); _titleBar.AddChild(hint); }
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.6f));
        hint.AddThemeFontSizeOverride("font_size", 11);

        // 拖拽支持
        _titleBar!.GuiInput += OnTitleBarInput;

        var closeBtn = _titleBar.GetNodeOrNull<Button>("CloseBtn");
        if (closeBtn == null) { closeBtn = new Button(); closeBtn.Text = "✕"; closeBtn.CustomMinimumSize = new Vector2(28, 0); _titleBar.AddChild(closeBtn); }
        closeBtn.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.8f));
        closeBtn.Pressed += () =>
        {
            _isVisible = false;
            _rootPanel!.Visible = false;
        };
    }

    private Vector2 _GlobalMousePos()
    {
        return GetViewport()?.GetMousePosition() ?? Vector2.Zero;
    }

    private void OnTitleBarInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            _dragging = mb.Pressed;
            if (_dragging && _rootPanel != null)
                _dragOffset = _rootPanel.Position - _GlobalMousePos();
        }
    }

    private void OnRootPanelInput(InputEvent @event)
    {
        if (_dragging && @event is InputEventMouseMotion && _rootPanel != null)
        {
            _rootPanel.Position = _dragOffset + _GlobalMousePos();
        }
    }



    // ─── 样式 ────────────────────────────────

    /// <summary>统一搜索框样式</summary>
    internal static void _StyleSearchField(LineEdit field)
    {
        var searchBg = new StyleBoxFlat();
        searchBg.BgColor = new Color(0.04f, 0.04f, 0.06f, 0.9f);
        searchBg.BorderColor = new Color(0.3f, 0.25f, 0.12f, 0.5f);
        searchBg.SetBorderWidthAll(1);
        searchBg.CornerRadiusBottomLeft = 4;
        searchBg.CornerRadiusBottomRight = 4;
        searchBg.CornerRadiusTopLeft = 4;
        searchBg.CornerRadiusTopRight = 4;
        searchBg.ContentMarginLeft = 6;
        searchBg.ContentMarginRight = 6;
        field.AddThemeStyleboxOverride("normal", searchBg);
        field.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.9f));
        field.AddThemeColorOverride("placeholder_color", new Color(0.4f, 0.4f, 0.5f));
        field.AddThemeFontSizeOverride("font_size", 12);
    }

    /// <summary>统一小按钮样式</summary>
    internal static void _StyleSmallButton(Button btn, bool isMarked)
    {
        var s = new StyleBoxFlat();
        s.BgColor = isMarked ? new Color(0.15f, 0.25f, 0.1f, 0.8f) : new Color(0.1f, 0.1f, 0.15f, 0.8f);
        s.BorderColor = isMarked ? new Color(0.3f, 0.7f, 0.2f, 0.6f) : new Color(0.3f, 0.25f, 0.12f, 0.4f);
        s.SetBorderWidthAll(1);
        s.CornerRadiusBottomLeft = 3;
        s.CornerRadiusBottomRight = 3;
        s.CornerRadiusTopLeft = 3;
        s.CornerRadiusTopRight = 3;
        s.ContentMarginLeft = 4;
        s.ContentMarginRight = 4;
        btn.AddThemeStyleboxOverride("normal", s);

        var hover = new StyleBoxFlat();
        hover.BgColor = isMarked ? new Color(0.2f, 0.35f, 0.15f, 0.9f) : new Color(0.18f, 0.16f, 0.12f, 0.9f);
        hover.BorderColor = isMarked ? new Color(0.4f, 0.8f, 0.3f, 0.8f) : new Color(0.5f, 0.4f, 0.15f, 0.6f);
        hover.SetBorderWidthAll(1);
        hover.CornerRadiusBottomLeft = 3;
        hover.CornerRadiusBottomRight = 3;
        hover.CornerRadiusTopLeft = 3;
        hover.CornerRadiusTopRight = 3;
        btn.AddThemeStyleboxOverride("hover", hover);

        btn.AddThemeFontSizeOverride("font_size", 11);
        btn.AddThemeColorOverride("font_color", isMarked ? new Color(0.4f, 1f, 0.3f) : new Color(0.75f, 0.75f, 0.85f));
        btn.CustomMinimumSize = new Vector2(62, 22);
    }

    private void _RefreshQueueTab()
    {
        if (_queueTabContent == null) return;
        foreach (var child in _queueTabContent.GetChildren())
            _queueTabContent.RemoveChild(child);

        var outerScroll = new ScrollContainer();
        outerScroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        outerScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        var vbox = new VBoxContainer();
        vbox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        // 清除按钮行 + 不消耗开关
        var clearRow = new HBoxContainer();
        clearRow.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        clearRow.AddThemeConstantOverride("separation", 4);
        Button MkClear(string t, System.Action a) { var b = new Button(); b.Text = t; b.CustomMinimumSize = new Vector2(0, 20); b.AddThemeFontSizeOverride("font_size", 10); b.AddThemeColorOverride("font_color", new Color(0.7f, 0.3f, 0.3f)); b.Pressed += () => { a(); CardSpawnService.QueueDirty = true; _RefreshQueueTab(); }; return b; }
        clearRow.AddChild(MkClear(I18n.T("clear_all"), () => { CardSpawnService.SelectedCardIdsForReward.Clear(); CardSpawnService.MarkedRelicIds.Clear(); CardSpawnService.MarkedPotionIds.Clear(); CardSpawnService.RebuildReplacementQueue(); CardSpawnService.RebuildRelicQueue(); CardSpawnService.RebuildPotionQueue(); }));
        clearRow.AddChild(MkClear(I18n.T("clear_cards"), () => { CardSpawnService.SelectedCardIdsForReward.Clear(); CardSpawnService.RebuildReplacementQueue(); }));
        clearRow.AddChild(MkClear(I18n.T("clear_relics"), () => { CardSpawnService.MarkedRelicIds.Clear(); CardSpawnService.RebuildRelicQueue(); }));
        clearRow.AddChild(MkClear(I18n.T("clear_potions"), () => { CardSpawnService.MarkedPotionIds.Clear(); CardSpawnService.RebuildPotionQueue(); }));
        // 不消耗开关（放在清空行右侧）
        var reuseToggle = new CheckBox();
        reuseToggle.Text = I18n.T("queue_reuse");
        reuseToggle.AddThemeFontSizeOverride("font_size", 10);
        reuseToggle.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f));
        reuseToggle.AddThemeColorOverride("icon_color", new Color(0.9f, 0.9f, 0.9f));
        reuseToggle.ButtonPressed = CardSpawnService.ReuseOnReroll;
        reuseToggle.Toggled += (on) => CardSpawnService.ReuseOnReroll = on;
        clearRow.AddChild(reuseToggle);
        vbox.AddChild(clearRow);

        var hbox = new HBoxContainer();
        hbox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        hbox.AddThemeConstantOverride("separation", 4);

        _BuildQueueColumn(hbox, I18n.T("column_cards"), CardSpawnService.SelectedCardIdsForReward,
            new Color(0.7f, 0.7f, 0.2f), id => CardSpawnService.FindCard(id)?.Title ?? id);
        hbox.AddChild(new VSeparator());
        _BuildEnchantColumn(hbox);
        hbox.AddChild(new VSeparator());
        _BuildQueueColumn(hbox, I18n.T("column_relics"), CardSpawnService.MarkedRelicIds,
            new Color(0.2f, 0.7f, 0.7f), id => ModelDb.AllRelics.FirstOrDefault(r => r.Id.Entry == id)?.Title.GetFormattedText() ?? id);
        hbox.AddChild(new VSeparator());
        _BuildQueueColumn(hbox, I18n.T("column_potions"), CardSpawnService.MarkedPotionIds,
            new Color(0.7f, 0.2f, 0.7f), id => ModelDb.AllPotions.FirstOrDefault(p => p.Id.Entry == id)?.Title.GetFormattedText() ?? id);

        vbox.AddChild(hbox);
        outerScroll.AddChild(vbox);
        _queueTabContent.AddChild(outerScroll);
    }

    private void _BuildQueueColumn(Control parent, string title, List<string> items, Color headerColor, Func<string, string> getName, System.Action<int>? onRemove = null)
    {
        var col = new VBoxContainer();
        col.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        col.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        var header = new Label();
        header.Text = $"{title}（{items.Count}）";
        header.AddThemeFontSizeOverride("font_size", 12);
        header.AddThemeColorOverride("font_color", headerColor);
        header.CustomMinimumSize = new Vector2(0, 20);
        col.AddChild(header);

        if (items.Count == 0)
        {
            var empty = new Label();
            empty.Text = I18n.T("empty_list");
            empty.AddThemeColorOverride("font_color", new Color(0.4f, 0.4f, 0.4f));
            empty.AddThemeFontSizeOverride("font_size", 11);
            col.AddChild(empty);
        }
        else
        {
            for (int i = 0; i < items.Count; i++)
            {
                var row = new HBoxContainer();
                row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                row.CustomMinimumSize = new Vector2(0, 22);
                var pos = new Label();
                pos.Text = $"#{i + 1}";
                pos.CustomMinimumSize = new Vector2(24, 0);
                pos.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.3f));
                pos.AddThemeFontSizeOverride("font_size", 11);
                row.AddChild(pos);
                var name = new Label();
                name.Text = getName(items[i]);
                name.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
                name.AddThemeFontSizeOverride("font_size", 11);
                row.AddChild(name);
                var delIdx = i;
                var delBtn = new Button();
                delBtn.Text = I18n.T("delete");
                                delBtn.CustomMinimumSize = new Vector2(28, 18);
                                delBtn.AddThemeFontSizeOverride("font_size", 10);
                delBtn.AddThemeColorOverride("font_color", new Color(0.8f, 0.2f, 0.2f));
                delBtn.Pressed += () => { if (delIdx < items.Count) { items.RemoveAt(delIdx); CardSpawnService.RebuildReplacementQueue(); CardSpawnService.RebuildRelicQueue(); CardSpawnService.RebuildPotionQueue(); CardSpawnService.QueueDirty = true; try { System.IO.File.AppendAllText(CardRewardPatch.DebugLogPath, $"[Diag] QueueRemove: removed index {delIdx}, count now {items.Count}\n"); } catch { } _RefreshQueueTab(); } };
                row.AddChild(delBtn);
                col.AddChild(row);
            }
        }
        parent.AddChild(col);
    }

    private void _BuildEnchantColumn(Control parent)
    {
        var col = new VBoxContainer();
        col.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        col.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        // 标题
        var h = new Label();
        h.Text = I18n.T("enchant_header");
        h.AddThemeFontSizeOverride("font_size", 12);
        h.AddThemeColorOverride("font_color", new Color(0.9f, 0.7f, 0.3f));
        col.AddChild(h);

        // 升级复选框
        var upg = new CheckBox();
        upg.Text = I18n.T("upgrade");
        upg.AddThemeFontSizeOverride("font_size", 10);
        upg.AddThemeColorOverride("font_color", new Color(0.8f, 0.9f, 0.7f));
        upg.ButtonPressed = CardSpawnService.MarkedCardsUpgraded;
        upg.Toggled += (on) => CardSpawnService.MarkedCardsUpgraded = on;
        col.AddChild(upg);

        // 附魔列表
        var order = CardSpawnService.EnchantmentOrder;
        for (int i = 0; i < order.Count; i++)
        {
            var eid = order[i];
            var row = new HBoxContainer();
            row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddThemeConstantOverride("separation", 2);

            // Radio dot（放大 0.5 倍）
            var isSelected = CardSpawnService.SelectedEnchantmentId == eid;
            var dot = new Button();
            dot.Text = isSelected ? "●" : "○";
            dot.AddThemeFontSizeOverride("font_size", 14);
            dot.AddThemeColorOverride("font_color", isSelected ? new Color(0.3f, 1f, 0.3f) : new Color(0.6f, 0.6f, 0.6f));
            dot.CustomMinimumSize = new Vector2(24, 24);
            var captured = eid;
            dot.Pressed += () =>
            {
                CardSpawnService.SelectedEnchantmentId = (CardSpawnService.SelectedEnchantmentId == captured) ? null : captured;
                CardSpawnService.QueueDirty = true;
                _RefreshQueueTab();
            };
            row.AddChild(dot);

            // 名称（大一号字体 + 悬停提示）
            var name = new Label();
            name.Text = CardSpawnService.GetEnchantDisplayName(eid);
            name.AddThemeFontSizeOverride("font_size", 11);
            name.AddThemeColorOverride("font_color", new Color(0.75f, 0.85f, 0.9f));
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            name.MouseFilter = Control.MouseFilterEnum.Stop;
            // 附魔描述 Tooltip
            try
            {
                var eType2 = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Enchantments." + eid);
                if (eType2 != null)
                {
                    var md = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.ModelDb");
                    var eg = md?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "Enchantment" && m.IsGenericMethod && m.GetParameters().Length == 0);
                    if (eg != null)
                    {
                        var spec = eg.MakeGenericMethod(eType2);
                        var canon = spec?.Invoke(null, null);
                        if (canon != null)
                        {
                            var descProp = canon.GetType().GetProperty("DynamicDescription");
                            var desc = descProp?.GetValue(canon);
                            if (desc != null)
                            {
                                var gft = desc.GetType().GetMethod("GetFormattedText", Type.EmptyTypes);
                                if (gft != null)
                                {
                                    var txt = gft.Invoke(desc, null) as string;
                                    if (!string.IsNullOrEmpty(txt))
                                        name.TooltipText = SpawnCheatPanel._CleanDesc(txt);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            row.AddChild(name);

            // ▲ ▼
            var upBtn = new Button();
            upBtn.Text = "▲";
            upBtn.CustomMinimumSize = new Vector2(14, 14);
            upBtn.AddThemeFontSizeOverride("font_size", 7);
            int idx = i;
            upBtn.Pressed += () => { CardSpawnService.MoveEnchantUp(idx); CardSpawnService.QueueDirty = true; _RefreshQueueTab(); };
            row.AddChild(upBtn);

            var downBtn = new Button();
            downBtn.Text = "▼";
            downBtn.CustomMinimumSize = new Vector2(14, 14);
            downBtn.AddThemeFontSizeOverride("font_size", 7);
            downBtn.Pressed += () => { CardSpawnService.MoveEnchantDown(idx); CardSpawnService.QueueDirty = true; _RefreshQueueTab(); };
            row.AddChild(downBtn);

            col.AddChild(row);
        }
        parent.AddChild(col);
    }
    internal static string _CleanDesc(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = raw;
        s = Regex.Replace(s, @"\[/?\w+\]", "");         // 去 BBCode 标签
        s = Regex.Replace(s, @"\[img\][^\[\]]*\[/img\]", ""); // 去图片
        s = Regex.Replace(s, @"\{[^}]*\}", "7");        // 动态变量→7
        return s.Trim();
    }

    /// <summary>通过反射获取模型描述文本</summary>
    internal static string _GetModelDesc(object model)
    {
        if (model == null) return "";
        try
        {
            // 试多个属性名（DynamicDescription / Description / description）
            var names = new[] { "DynamicDescription", "Description" };
            foreach (var n in names)
            {
                var dp = model.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                if (dp == null) continue;
                var dv = dp.GetValue(model);
                if (dv == null) continue;
                var gf = dv.GetType().GetMethod("GetFormattedText", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (gf == null) continue;
                var txt = gf.Invoke(dv, null) as string;
                if (!string.IsNullOrEmpty(txt)) return txt;
            }
            return "";
        }
        catch { return ""; }
    }
}

// ─── 各 Tab 内容类 ──────────────────────────

/// <summary>卡牌标记 Tab — 搜索列表 + 标记 Populate 注入</summary>
public partial class CardTabContent : Control
{
    // ─── 筛选状态 ──────────────────────────
    private LineEdit? _searchField;
    private int _charIdx;
    private readonly List<CharacterModel> _characters = new();
    private string? _rarityFilter; // null=全部

    // ─── UI 控件 ───────────────────────────
    private Control? _cardGrid;
    private Label? _markCountLabel;
    private Label? _filterSummary;
    private Label? _countLabel;
    private Label? _queueLabel;
    private ScrollContainer? _scroll;
    private HBoxContainer? _charChips;
    private HBoxContainer? _rarityChips;

    private const int GridCols = 5;

    /// <summary>稀有度颜色表 — 按你的要求：基本/普通=灰，罕见=浅蓝，稀有=淡黄</summary>
    private static readonly Dictionary<string, Color> _rarityColors = new()
    {
        ["Basic"] = new Color(0.65f, 0.65f, 0.7f),     // 灰（同普通）
        ["Common"] = new Color(0.65f, 0.65f, 0.7f),    // 灰
        ["Uncommon"] = new Color(0.35f, 0.7f, 1f),     // 浅蓝
        ["Rare"] = new Color(1f, 0.85f, 0.35f),        // 淡黄
        ["Ancient"] = new Color(0.7f, 0.5f, 0.15f),    // 金棕（保留用于筛选标签）
        ["Curse"] = new Color(0.8f, 0.3f, 0.3f),       // 红（保留）
    };

    /// <summary>选中态背景色</summary>
    private static readonly Dictionary<string, Color> _rarityBgMarked = new()
    {
        ["Basic"] = new Color(0.15f, 0.35f, 0.08f, 0.5f),
        ["Common"] = new Color(0.3f, 0.3f, 0.28f, 0.5f),
        ["Uncommon"] = new Color(0.2f, 0.18f, 0.45f, 0.5f),
        ["Rare"] = new Color(0.05f, 0.25f, 0.45f, 0.5f),
        ["Ancient"] = new Color(0.35f, 0.2f, 0.03f, 0.5f),
        ["Curse"] = new Color(0.4f, 0.1f, 0.1f, 0.5f),
    };

    /// <summary>卡牌池 ID → 英雄代表色（运行时构建，从 DeckEntryCardColor 自动提取）</summary>
    private readonly Dictionary<string, Color> _poolColorMap = new();

    /// <summary>自动分配色板（MOD 角色没有自定义颜色时 fallback）</summary>
    private static readonly Color[] _fallbackColors = new[]
    {
        new Color(0.85f, 0.15f, 0.15f),   // 红
        new Color(0.15f, 0.7f, 0.2f),     // 绿
        new Color(0.2f, 0.5f, 0.9f),      // 蓝
        new Color(0.9f, 0.55f, 0.05f),    // 橙
        new Color(0.7f, 0.2f, 0.7f),      // 紫
        new Color(0.1f, 0.75f, 0.75f),    // 青
        new Color(0.85f, 0.85f, 0.1f),    // 黄
        new Color(0.9f, 0.3f, 0.5f),      // 粉
    };

    /// <summary>稀有度列表（筛选行用）</summary>
    private static readonly (string label, string? rarity)[] _rarityOpts = new[]
    {
        (I18n.T("filter_all"), null as string),
        (I18n.T("rarity_basic"), "Basic"),
        (I18n.T("rarity_common"), "Common"),
        (I18n.T("rarity_uncommon"), "Uncommon"),
        (I18n.T("rarity_rare"), "Rare"),
        (I18n.T("rarity_ancient"), "Ancient"),
        (I18n.T("rarity_curse"), "Curse"),
    };

    public void Init()
    {
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(0, 350);

        var vbox = new VBoxContainer();
        vbox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        vbox.AddThemeConstantOverride("separation", 4);
        AddChild(vbox);

        // ── 筛选行 1：角色 ──
        _charChips = new HBoxContainer();
        _charChips.AddThemeConstantOverride("separation", 4);
        vbox.AddChild(_charChips);
        _RebuildCharChips();

        // ── 筛选行 2：稀有度（左）+ 搜索（右）──
        var row2 = new HBoxContainer();
        row2.AddThemeConstantOverride("separation", 4);
        row2.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        vbox.AddChild(row2);

        _rarityChips = new HBoxContainer();
        _rarityChips.AddThemeConstantOverride("separation", 3);
        row2.AddChild(_rarityChips);
        _RebuildRarityChips();

        // 搜索框（最右侧，固定宽度）
        _searchField = new LineEdit();
        _searchField.PlaceholderText = "🔍 " + I18n.T("search_card");
        _searchField.CustomMinimumSize = new Vector2(140, 22);
        _searchField.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        _searchField.AddThemeFontSizeOverride("font_size", 11);
        _searchField.TextChanged += _ => Refresh();
        row2.AddChild(_searchField);

        _countLabel = new Label();
        _countLabel.Text = "0";
        _countLabel.AddThemeFontSizeOverride("font_size", 11);
        _countLabel.AddThemeColorOverride("font_color", new Color(0.4f, 0.4f, 0.5f));
        _countLabel.CustomMinimumSize = new Vector2(30, 0);
        _countLabel.HorizontalAlignment = HorizontalAlignment.Right;
        row2.AddChild(_countLabel);

        // 替换队列指示器
        _queueLabel = new Label();
        _queueLabel.Text = "";
        _queueLabel.AddThemeFontSizeOverride("font_size", 10);
        _queueLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.8f, 0.6f));
        _queueLabel.ClipText = true;
        _queueLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        row2.AddChild(_queueLabel);

        // ── 内容区（Scroll + 底部 Dock 在同一个容器内，避免 Dock 溢出）──
        var contentVbox = new VBoxContainer();
        contentVbox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        contentVbox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        contentVbox.AddThemeConstantOverride("separation", 4);
        vbox.AddChild(contentVbox);

        _scroll = new ScrollContainer();
        _scroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _scroll.CustomMinimumSize = new Vector2(0, 250);
        _scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        contentVbox.AddChild(_scroll);

        _cardGrid = new Control();
        _cardGrid.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _scroll.AddChild(_cardGrid);

        // ── 底部 Dock 栏（在 contentVbox 内，共享 ExpandFill 空间，不溢出）──
        // 用 MarginContainer（Container 子类）替代 Panel，让内部 HBox 的 SizeFlags 生效
        var bottomDock = new MarginContainer();
        bottomDock.CustomMinimumSize = new Vector2(0, 34);
        bottomDock.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var dockStyle = new StyleBoxFlat();
        dockStyle.BgColor = new Color(0.04f, 0.04f, 0.07f, 0.95f);
        dockStyle.BorderColor = new Color(0.25f, 0.2f, 0.1f, 0.6f);
        dockStyle.SetBorderWidthAll(1);
        dockStyle.CornerRadiusBottomLeft = 4;
        dockStyle.CornerRadiusBottomRight = 4;
        dockStyle.ContentMarginLeft = 0;  // 边距由 MarginContainer 处理
        dockStyle.ContentMarginRight = 0;
        bottomDock.AddThemeStyleboxOverride("panel", dockStyle);
        bottomDock.AddThemeConstantOverride("margin_left", 8);
        bottomDock.AddThemeConstantOverride("margin_right", 8);
        contentVbox.AddChild(bottomDock);

        var dockHbox = new HBoxContainer();
        dockHbox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        dockHbox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        dockHbox.AddThemeConstantOverride("separation", 10);
        bottomDock.AddChild(dockHbox);

        // 左侧：筛选摘要
        _filterSummary = new Label();
        _filterSummary.AddThemeFontSizeOverride("font_size", 11);
        _filterSummary.AddThemeColorOverride("font_color", new Color(0.4f, 0.4f, 0.5f));
        _filterSummary.VerticalAlignment = VerticalAlignment.Center;
        dockHbox.AddChild(_filterSummary);

        // 弹性分隔（把筛选摘要推到左侧）
        var dockSpacer = new Control();
        dockSpacer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        dockSpacer.MouseFilter = Control.MouseFilterEnum.Ignore;
        dockHbox.AddChild(dockSpacer);

        Refresh();
    }

    // ─── 角色标签 ───────────────────
    private const int _charIdxColorless = -1; // 无色卡
    private const int _charIdxStatus = -2;    // 状态卡
    private bool _isCharOverflow;   // 角色太多时改用下拉框
    private OptionButton? _charDropdown;

    private void _RebuildCharChips()
    {
        _charChips!.ClearChildren();
        _characters.Clear();
        _characters.Add(null!); // index 0 = 全部
        foreach (var ch in ModelDb.AllCharacters)
            _characters.Add(ch);

        // 如果字符数（含"全部"）+ 2个特殊标签 > 9 就自动换成下拉框
        _isCharOverflow = _characters.Count + 2 > 9;

        if (_isCharOverflow)
        {
            _BuildCharDropdown();
        }
        else
        {
            for (int i = 0; i < _characters.Count; i++)
            {
                var idx = i;
                var label = _characters[i] != null
                    ? _characters[i].Title.GetFormattedText()
                    : I18n.T("filter_all");
                var btn = new Button();
                btn.Text = label;
                btn.CustomMinimumSize = new Vector2(0, 22);
                btn.AddThemeFontSizeOverride("font_size", 11);
                btn.Pressed += () => { _charIdx = idx; _UpdateChipStyles(); Refresh(); };
                _charChips.AddChild(btn);
            }

            // 无色卡 + 状态卡筛选按钮
            BuildSpecialChip(I18n.T("filter_colorless"), _charIdxColorless);
            BuildSpecialChip(I18n.T("filter_status"), _charIdxStatus);
        }

        _UpdateChipStyles();

        // 构建英雄池 ID → 颜色映射（从 CardPool.DeckEntryCardColor 自动提取）
        _poolColorMap.Clear();
        for (int i = 1; i < _characters.Count; i++)
        {
            var ch = _characters[i];
            if (ch?.CardPool?.Id?.Entry == null) continue;
            // 优先用游戏自带的 DeckEntryCardColor（MOD 角色如果配置了就直接生效）
            var color = ch.CardPool.DeckEntryCardColor;
            // Godot Color 的默认值是 (0,0,0,1)，如果是纯黑可能没设 → 走 fallback
            if (color.R + color.G + color.B < 0.01f)
                color = _fallbackColors[(i - 1) % _fallbackColors.Length];
            _poolColorMap[ch.CardPool.Id.Entry] = color;
        }
    }

    // ─── 稀有度标签 ────────────────
    private void _RebuildRarityChips()
    {
        _rarityChips!.ClearChildren();
        for (int i = 0; i < _rarityOpts.Length; i++)
        {
            var idx = i;
            var btn = new Button();
            btn.Text = _rarityOpts[i].label;
            btn.CustomMinimumSize = new Vector2(0, 22);
            btn.AddThemeFontSizeOverride("font_size", 11);
            btn.Pressed += () =>
            {
                _rarityFilter = _rarityOpts[idx].rarity;
                _UpdateRarityChipStyles();
                Refresh();
            };
            _rarityChips.AddChild(btn);
        }
        _UpdateRarityChipStyles();
    }

    private void BuildSpecialChip(string text, int charIdx)
    {
        var btn = new Button();
        btn.Text = text;
        btn.CustomMinimumSize = new Vector2(0, 22);
        btn.AddThemeFontSizeOverride("font_size", 11);
        btn.Pressed += () => { _charIdx = charIdx; _UpdateChipStyles(); Refresh(); };
        _charChips!.AddChild(btn);
    }

    private void _BuildCharDropdown()
    {
        _charDropdown = new OptionButton();
        _charDropdown.CustomMinimumSize = new Vector2(130, 22);
        _charDropdown.AddThemeFontSizeOverride("font_size", 11);

        for (int i = 0; i < _characters.Count; i++)
        {
            var label = _characters[i] != null
                ? _characters[i].Title.GetFormattedText()
                : I18n.T("filter_all");
            _charDropdown.AddItem(label, i); // id = _charIdx
        }
        _charDropdown.AddItem(I18n.T("filter_colorless"), _charIdxColorless);
        _charDropdown.AddItem(I18n.T("filter_status"), _charIdxStatus);
        _charDropdown.Selected = 0;

        _charDropdown.ItemSelected += (idx) =>
        {
            _charIdx = _charDropdown.GetItemId((int)idx);
            _UpdateChipStyles();
            Refresh();
        };
        _charChips!.AddChild(_charDropdown);
    }

    private void _UpdateChipStyles()
    {
        if (_isCharOverflow && _charDropdown != null)
        {
            // 下拉模式：同步选中项
            for (int i = 0; i < _charDropdown.ItemCount; i++)
            {
                if (_charDropdown.GetItemId(i) == _charIdx)
                { _charDropdown.Selected = i; break; }
            }
            _charDropdown.AddThemeColorOverride("font_color", new Color(1f, 0.88f, 0.35f));
        }
        else
        {
            int total = _charChips!.GetChildCount();
            for (int i = 0; i < total; i++)
            {
                var btn = _charChips.GetChild<Button>(i);
                bool sel;
                if (i < _characters.Count)
                    sel = _charIdx == i;
                else if (i == _characters.Count)
                    sel = _charIdx == _charIdxColorless;
                else
                    sel = _charIdx == _charIdxStatus;
                btn.AddThemeColorOverride("font_color", sel ? new Color(1f, 0.88f, 0.35f) : new Color(0.6f, 0.6f, 0.7f));
            }
        }
        _UpdateFilterSummary();
    }

    private void _UpdateRarityChipStyles()
    {
        for (int i = 0; i < _rarityChips!.GetChildCount(); i++)
        {
            var btn = _rarityChips.GetChild<Button>(i);
            bool sel = _rarityOpts[i].rarity == _rarityFilter;
            btn.AddThemeColorOverride("font_color", sel ? new Color(1f, 0.88f, 0.35f) : new Color(0.6f, 0.6f, 0.7f));
        }
        _UpdateFilterSummary();
    }

    private void _UpdateFilterSummary()
    {
        if (_filterSummary == null) return;
        var parts = new List<string>();
        if (_charIdx == _charIdxColorless)
            parts.Add(I18n.T("filter_colorless"));
        else if (_charIdx == _charIdxStatus)
            parts.Add(I18n.T("filter_status"));
        else if (_charIdx > 0 && _charIdx < _characters.Count && _characters[_charIdx] != null)
            parts.Add(_characters[_charIdx].Title.GetFormattedText());
        if (_rarityFilter != null)
            parts.Add(_rarityOpts.First(o => o.rarity == _rarityFilter).label);
        _filterSummary.Text = parts.Count > 0 ? string.Join(" · ", parts) : "";
    }

    // ─── 刷新网格 ───────────────────
    public void Refresh()
    {
        if (_cardGrid == null) return;
        _cardGrid.ClearChildren();

        string filter = _searchField?.Text.Trim().ToUpperInvariant() ?? "";

        var cards = ModelDb.AllCards
            .Where(c => string.IsNullOrEmpty(filter) ||
                        c.Id.Entry.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(c =>
            {
                if (_charIdx == _charIdxColorless) return c.Pool is ColorlessCardPool;
                if (_charIdx == _charIdxStatus)
                {
                    string? pid = c.Pool?.Id?.Entry;
                    return c.Rarity.ToString() != "Ancient"
                        && !(c.Pool is ColorlessCardPool)
                        && !(pid != null && _poolColorMap.ContainsKey(pid));
                }
                if (_charIdx <= 0) return true;
                if (_charIdx >= _characters.Count || _characters[_charIdx] == null) return true;
                var poolId = _characters[_charIdx].CardPool?.Id?.Entry;
                return poolId != null && c.Pool?.Id?.Entry == poolId;
            })
            .Where(c => _rarityFilter == null || c.Rarity.ToString() == _rarityFilter)
            .OrderBy(c => c.Id.Entry)
            .Take(200)
            .ToList();

        _countLabel!.Text = $"{cards.Count}";
        _UpdateMarkCount();

        var markedSet = new HashSet<string>(CardSpawnService.SelectedCardIdsForReward);

        // 手动分行布局，每行 GridCols 列（替代 GridContainer 自动排版）
        // K2 手动定位：不用任何 Container，SetPosition + SetSize
        int cellW = 140;
        int cellH = 48;
        int gap = 4;
        int padL = 4;
        int total = cards.Count;

        // 延时一帧确保布局尺寸就绪（用 CreateTimer 避免 AddChild 失败）
        var tree = GetTree();
        if (tree == null) return;
        tree.CreateTimer(0.05f).Timeout += () =>
        {
            if (_cardGrid == null) return;
            float availW = _cardGrid.GetRect().Size.X;
            if (availW <= 0) availW = 780;
            int cols = Mathf.Max(1, (int)((availW + gap) / (cellW + gap)));
            // 输出全链路尺寸（通过父链回溯）
            var p1 = GetParent() as Control; // Tab Content Area
            var p2 = p1?.GetParent() as Control; // VBox
            var p3 = p2?.GetParent() as Control; // Root Control
            // 设置 _cardGrid 最小尺寸让 ScrollContainer 知道可滚动
            int gridRows = (total + cols - 1) / cols;
            float gridH = gridRows * (cellH + gap);
            _cardGrid.CustomMinimumSize = new Vector2(availW, gridH);

            System.IO.File.AppendAllText(CardRewardPatch.DebugLogPath,
                $"[{System.DateTime.Now:HH:mm:ss}] availW={availW} cols={cols} total={total} rows={gridRows} gridH={gridH}\n" +
                $"  cardTab={GetRect().Size} p1(tabArea)={p1?.GetRect().Size ?? Vector2.Zero} p2(vbox)={p2?.GetRect().Size ?? Vector2.Zero} p3(root)={p3?.GetRect().Size ?? Vector2.Zero}\n" +
                $"  scroll={_scroll?.GetRect().Size ?? Vector2.Zero} cardGrid={_cardGrid.GetRect().Size}\n");

            foreach (Node child in _cardGrid.GetChildren())
                child.QueueFree();

            for (int i = 0; i < total; i++)
            {
                int col = i % cols;
                int row = i / cols;
                float x = padL + col * (cellW + gap);
                float y = row * (cellH + gap);

                var card = cards[i];
                string cId = card.Id.Entry;
                bool isM = markedSet.Contains(cId);
                var rn = card.Rarity.ToString();
                var rc = _rarityColors.ContainsKey(rn) ? _rarityColors[rn] : new Color(0.3f, 0.3f, 0.35f);
                // 英雄代表色（卡牌所属英雄的边框色）
                string? poolId = card.Pool?.Id?.Entry;
                Color charColor = poolId != null && _poolColorMap.TryGetValue(poolId, out var cc)
                    ? cc : new Color(0.3f, 0.3f, 0.35f);

                // ─── 卡牌分类：确定左侧色条颜色 ───
                // 1️⃣ 远古之民卡（稀有度为 Ancient）→ 黑色条
                bool isAncient = rn == "Ancient";
                // 2️⃣ 无色卡（ColorlessCardPool）→ 深灰色条
                bool isColorless = !isAncient && card.Pool is ColorlessCardPool;
                // 3️⃣ 英雄卡（在 _poolColorMap 中有映射）→ 英雄色条（已有 charColor）
                bool isHero = !isAncient && !isColorless && poolId != null && _poolColorMap.ContainsKey(poolId);
                // 4️⃣ 状态卡（其余全部）→ 紫色条

                // 左侧色条颜色
                Color barColor;
                if (isAncient)
                    barColor = new Color(0.02f, 0.02f, 0.02f); // 黑
                else if (isColorless)
                    barColor = new Color(0.2f, 0.2f, 0.2f);    // 深灰
                else if (isHero)
                    barColor = charColor;                       // 英雄色
                else
                    barColor = new Color(0.55f, 0.2f, 0.6f);   // 紫（状态卡）

                // 边框颜色 = 标记态金色，否则同色条
                Color borderColor = isM ? new Color(1f, 0.85f, 0.3f) : barColor;

                // 文字颜色
                Color textColor;
                if (isAncient || (!isHero && !isColorless))
                {
                    // 远古之民卡 / 状态卡 → 灰色文字
                    textColor = new Color(0.55f, 0.55f, 0.6f);
                }
                else if (isColorless)
                {
                    // 无色卡 → 稀有度色（浅蓝/淡黄）
                    textColor = rc;
                }
                else
                {
                    // 英雄卡 → 稀有度色
                    textColor = rc;
                }

                var cell = new Panel();
                cell.Name = cId;  // 用于内联查找
                cell.SetPosition(new Vector2(x, y));
                cell.SetSize(new Vector2(cellW, cellH));
                cell.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
                cell.TooltipText = _CardTooltip(card);

                var s = new StyleBoxFlat();
                s.BgColor = isM
                    ? (_rarityBgMarked.ContainsKey(rn) ? _rarityBgMarked[rn] : new Color(0.15f, 0.15f, 0.2f, 0.6f))
                    : new Color(0.08f, 0.08f, 0.12f, 0.8f);
                s.BorderColor = borderColor;
                s.SetBorderWidthAll(isM ? 2 : 1);
                s.CornerRadiusBottomLeft = 4;
                s.CornerRadiusBottomRight = 4;
                s.CornerRadiusTopLeft = 4;
                s.CornerRadiusTopRight = 4;
                cell.AddThemeStyleboxOverride("panel", s);

                var captured = cId;
                cell.GuiInput += (InputEvent ev) =>
                {
                    if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                        _ToggleMark(captured);
                };

                // 内部布局还用 HBoxContainer（但只一层）
                var hbox = new HBoxContainer();
                hbox.MouseFilter = Control.MouseFilterEnum.Ignore;  // 让点击穿透到 cell
                hbox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                hbox.AddThemeConstantOverride("separation", 4);
                cell.AddChild(hbox);

                var bar = new Panel();
                bar.CustomMinimumSize = new Vector2(4, cellH);
                var bs = new StyleBoxFlat();
                bs.BgColor = barColor;  // 左侧色条 = 分类色（英雄/远古/无色/状态）
                bar.AddThemeStyleboxOverride("panel", bs);
                hbox.AddChild(bar);

                var lbl = new Label();
                lbl.Text = card.Title;
                lbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                lbl.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
                lbl.VerticalAlignment = VerticalAlignment.Center;
                lbl.AddThemeFontSizeOverride("font_size", 12);
                lbl.AddThemeColorOverride("font_color", textColor);  // 名字颜色 = 分类对应色
                lbl.ClipText = true;
                lbl.AutowrapMode = TextServer.AutowrapMode.Off;
                lbl.MouseFilter = Control.MouseFilterEnum.Ignore;  // 穿透点击到 cell
                hbox.AddChild(lbl);

                if (isM)
                {
                    var bd = new Label();
                    bd.Text = "\u2713";
                    bd.CustomMinimumSize = new Vector2(18, 18);
                    bd.AddThemeFontSizeOverride("font_size", 13);
                    bd.AddThemeColorOverride("font_color", new Color(0.3f, 1f, 0.3f));
                    hbox.AddChild(bd);
                }
                else
                {
                    var sp2 = new Control();
                    sp2.CustomMinimumSize = new Vector2(18, 18);
                    hbox.AddChild(sp2);
                }

                // 收藏星标
                bool isFav = CardSpawnService.FavoriteCardIds.Contains(cId);
                var star = GodotExtensions._MakeStarButton(isFav, CardSpawnService.FavoriteCardIds, cId);
                hbox.AddChild(star);

                _cardGrid.AddChild(cell);
            }
        };
    }

    /// <summary>生成干净的卡牌 Tooltip（去 BBCode / 动态变量）</summary>
    private static string _CardTooltip(CardModel card)
    {
        if (card == null) return "";
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{card.Title}");
            var cost = "?";
            try
            {
                var ec = card.EnergyCost;
                if (ec != null)
                {
                    var cProp = ec.GetType().GetProperty("Canonical");
                    if (cProp != null) cost = cProp.GetValue(ec)?.ToString() ?? "X";
                }
            }
            catch { }
            sb.AppendLine(string.Format(I18n.T("cost_type"), cost, card.Type));
            var desc = "";
            try { desc = card.Description?.GetFormattedText() ?? ""; } catch { }
            if (!string.IsNullOrEmpty(desc))
            {
                desc = SpawnCheatPanel._CleanDesc(desc);
                if (!string.IsNullOrEmpty(desc))
                    sb.AppendLine(desc);
            }
            sb.Append($"({card.Rarity})");
            return sb.ToString();
        }
        catch { return card.Title ?? ""; }
    }

    private void _ToggleMark(string cardId)
    {
        var list = CardSpawnService.SelectedCardIdsForReward;
        bool wasMarked = list.Contains(cardId);
        if (wasMarked) list.Remove(cardId);
        else list.Add(cardId);
        CardSpawnService.RebuildReplacementQueue();
        _UpdateQueueLabel();
        _UpdateMarkCount();

        // 内联更新：只改这个 cell 的样式，不重绘整个网格
        if (_cardGrid == null) return;
        var cell = _cardGrid.GetNodeOrNull<Panel>(cardId);
        if (cell == null) return;

        bool nowMarked = !wasMarked;
        string rn = ""; // 从 cell 的 tooltip 暂存稀有度？不需要，直接算 marked 色
        var s = cell.GetThemeStylebox("panel") as StyleBoxFlat;
        if (s == null) return;

        s.BgColor = nowMarked
            ? new Color(0.15f, 0.15f, 0.2f, 0.6f)  // 简化标记背景
            : new Color(0.08f, 0.08f, 0.12f, 0.8f);
        s.BorderColor = nowMarked
            ? new Color(1f, 0.85f, 0.3f)  // 金色
            : new Color(0.3f, 0.3f, 0.35f);  // fallback 灰（实际会在下次全量刷新时恢复正确色）
        s.SetBorderWidthAll(nowMarked ? 2 : 1);

        // 更新标记指示器（hbox 的第 3 个子节点，索引 2）
        var hbox = cell.GetChildOrNull<HBoxContainer>(0);
        if (hbox == null || hbox.GetChildCount() < 4) return;
        var oldIndicator = hbox.GetChild(2);
        if (oldIndicator != null)
        {
            hbox.RemoveChild(oldIndicator);
            oldIndicator.QueueFree();
        }
        if (nowMarked)
        {
            var bd = new Label();
            bd.Text = "\u2713";
            bd.CustomMinimumSize = new Vector2(18, 18);
            bd.AddThemeFontSizeOverride("font_size", 13);
            bd.AddThemeColorOverride("font_color", new Color(0.3f, 1f, 0.3f));
            hbox.AddChild(bd);
            hbox.MoveChild(bd, 2);
        }
        else
        {
            var sp2 = new Control();
            sp2.CustomMinimumSize = new Vector2(18, 18);
            hbox.AddChild(sp2);
            hbox.MoveChild(sp2, 2);
        }
    }

    private void _ClearWithConfirm()
    {
        int count = CardSpawnService.SelectedCardIdsForReward.Count;
        if (count == 0) return;

        // 用游戏内弹窗确认
        var confirm = new AcceptDialog();
        confirm.DialogText = string.Format(I18n.T("clear_confirm"), count);
        confirm.Title = I18n.T("clear_title");
        confirm.OkButtonText = I18n.T("clear_btn");
        confirm.Confirmed += () =>
        {
            CardSpawnService.SelectedCardIdsForReward.Clear();
            Refresh();
        };
        // 挂到根 CanvasLayer 或 Root，确保弹窗在最上层
        var root = _FindRootPanel();
        if (root != null)
            root.AddChild(confirm);
        else
            AddChild(confirm);
        confirm.PopupCentered();
    }

    // 层级：CardTabContent → TabContainer → vbox → _rootPanel (Panel)
    private Panel? _FindRootPanel()
    {
        var p = GetParent();        // TabContainer
        if (p == null) return null;
        p = p.GetParent();          // vbox
        if (p == null) return null;
        p = p.GetParent();          // _rootPanel
        return p as Panel;
    }

    private void _UpdateMarkCount()
    {
        if (_markCountLabel == null) return;
        int count = CardSpawnService.SelectedCardIdsForReward.Count;
        int max = CardSpawnService.ExtraCardCount;
        if (max <= 0)
        {
            _markCountLabel.Text = string.Format(I18n.T("marked_inject_off"), count);
            _markCountLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
        }
        else
        {
            _markCountLabel.Text = string.Format(I18n.T("marked_count"), count, max);
            _markCountLabel.AddThemeColorOverride("font_color",
                count >= max ? new Color(0.9f, 0.7f, 0.1f) : new Color(0.3f, 0.9f, 0.3f));
        }
    }

    private void _UpdateQueueLabel()
    {
        if (_queueLabel == null) return;
        var queue = CardSpawnService.SelectedCardIdsForReward;
        if (queue.Count == 0) { _queueLabel.Text = ""; return; }
        var names = queue.Select(id =>
        {
            var c = CardSpawnService.FindCard(id);
            return c?.Title ?? id;
        }).ToList();
        _queueLabel.Text = string.Format(I18n.T("queue_label"), string.Join(" → ", names));
    }
}

internal static class GodotExtensions
{
    public static void ClearChildren(this Node parent)
    {
        foreach (Node child in parent.GetChildren())
            child.QueueFree();
    }

    /// <summary>创建星标收藏按钮（Button 形式，确保点击可靠）</summary>
    internal static Button _MakeStarButton(bool isFav, List<string> favList, string itemId)
    {
        var btn = new Button();
        btn.Text = isFav ? "\u2605" : "\u2606";
        btn.CustomMinimumSize = new Vector2(22, 18);
        btn.AddThemeFontSizeOverride("font_size", 14);
        btn.AddThemeColorOverride("font_color", isFav ? new Color(1f, 0.85f, 0.15f) : new Color(0.6f, 0.6f, 0.65f));
        // 扁平样式：无背景无边框
        var flat = new StyleBoxEmpty();
        btn.AddThemeStyleboxOverride("normal", flat);
        btn.AddThemeStyleboxOverride("hover", flat);
        btn.AddThemeStyleboxOverride("pressed", flat);
        btn.AddThemeStyleboxOverride("focus", flat);
        btn.Pressed += () =>
        {
            if (favList.Contains(itemId)) favList.Remove(itemId);
            else favList.Add(itemId);
            bool now = favList.Contains(itemId);
            btn.Text = now ? "\u2605" : "\u2606";
            btn.AddThemeColorOverride("font_color", now ? new Color(1f, 0.85f, 0.15f) : new Color(0.6f, 0.6f, 0.65f));
            CardSpawnService.SaveFavorites();
        };
        return btn;
    }
}

/// <summary>遗物生成 Tab</summary>
public partial class RelicTabContent : Control
{
    private const int GridCols = 5;
    private const int CellW = 140;
    private const int CellH = 48;

    private LineEdit? _searchField;
    private Control? _itemList;
    private Label? _countLabel;
    private int _relicRarityFilter = -1;

    public void Init()
    {
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var vbox = new VBoxContainer();
        vbox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        vbox.AddThemeConstantOverride("separation", 4);
        AddChild(vbox);

        var searchRow = new HBoxContainer();
        vbox.AddChild(searchRow);
        searchRow.AddChild(new Label { Text = I18n.T("search") });
        _searchField = new LineEdit();
        _searchField.PlaceholderText = I18n.T("search_relic");
        _searchField.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _searchField.TextChanged += _ => Refresh();
        searchRow.AddChild(_searchField);
        var rarityDropdown = new OptionButton();
        rarityDropdown.AddItem(I18n.T("filter_all"), -1);
        rarityDropdown.AddItem(I18n.T("rarity_common"), (int)RelicRarity.Common);
        rarityDropdown.AddItem(I18n.T("rarity_uncommon"), (int)RelicRarity.Uncommon);
        rarityDropdown.AddItem(I18n.T("rarity_rare"), (int)RelicRarity.Rare);
        rarityDropdown.AddItem(I18n.T("rarity_ancient"), (int)RelicRarity.Ancient);
        rarityDropdown.AddItem(I18n.T("rarity_relic_event"), (int)RelicRarity.Event);
        rarityDropdown.Selected = 0;
        rarityDropdown.CustomMinimumSize = new Vector2(58, 22);
        rarityDropdown.AddThemeFontSizeOverride("font_size", 11);
        rarityDropdown.ItemSelected += (idx) => { _relicRarityFilter = rarityDropdown.GetItemId((int)idx); Refresh(); };
        searchRow.AddChild(rarityDropdown);
        _countLabel = new Label { Text = "" };
        _countLabel.CustomMinimumSize = new Vector2(60, 0);
        _countLabel.HorizontalAlignment = HorizontalAlignment.Right;
        searchRow.AddChild(_countLabel);

        var scroll = new ScrollContainer();
        scroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        vbox.AddChild(scroll);

        _itemList = new Control();
        _itemList.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_itemList);

        Refresh();
    }

    public void Refresh()
    {
        if (_itemList == null) return;
        foreach (Node child in _itemList.GetChildren())
            child.QueueFree();

        string filter = _searchField?.Text.Trim().ToUpperInvariant() ?? "";
        var relics = ModelDb.AllRelics
            .Where(r => string.IsNullOrEmpty(filter) ||
                        r.Id.Entry.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(r => _relicRarityFilter < 0 || (int)r.Rarity == _relicRarityFilter)
            .ToList();

        _countLabel!.Text = string.Format(I18n.T("count_items"), relics.Count);

        // 和卡牌 Tab 一致：延时重建，避免 QueueFree + AddChild 同帧事件冲突
        var tree = GetTree();
        if (tree == null) return;
        tree.CreateTimer(0.05f).Timeout += () =>
        {
            if (_itemList == null) return;
            int cols = GridCols;
            int cellW = CellW;
            int cellH = CellH;
            int gap = 4;
            // 设置最小尺寸让 ScrollContainer 可滚动
            int gridRows = (relics.Count + cols - 1) / cols;
            _itemList.CustomMinimumSize = new Vector2(0, gridRows * (cellH + gap));
            for (int i = 0; i < relics.Count; i++)
            {
                var relic = relics[i];
                string rId = relic.Id.Entry;
                bool isMarked = CardSpawnService.MarkedRelicIds.Contains(rId);

                int x = (i % cols) * (cellW + 4);
                int y = (i / cols) * (cellH + 4);

                var cell = new Panel();
                cell.Name = rId;
                cell.SetPosition(new Vector2(x, y));
                cell.SetSize(new Vector2(cellW, cellH));
                cell.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
                cell.TooltipText = _RelicTooltip(relic);

                var borderColor = _GetRarityColor(relic);
                var s = new StyleBoxFlat();
                s.BgColor = isMarked ? new Color(0.12f, 0.25f, 0.12f, 0.8f) : new Color(0.08f, 0.08f, 0.12f, 0.8f);
                s.BorderColor = borderColor;
                s.SetBorderWidthAll(isMarked ? 2 : 1);
                s.CornerRadiusBottomLeft = 3;
                s.CornerRadiusBottomRight = 3;
                s.CornerRadiusTopLeft = 3;
                s.CornerRadiusTopRight = 3;
                cell.AddThemeStyleboxOverride("panel", s);

                var captured = rId;
                cell.GuiInput += (InputEvent ev) =>
                {
                    if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                        _ToggleRelicMark(captured);
                };

                var hbox = new HBoxContainer();
                hbox.MouseFilter = Control.MouseFilterEnum.Ignore;
                hbox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                hbox.AddThemeConstantOverride("separation", 4);
                cell.AddChild(hbox);

                var nameLabel = new Label();
                nameLabel.Text = relic.Title.GetFormattedText();
                nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                nameLabel.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
                nameLabel.VerticalAlignment = VerticalAlignment.Center;
                nameLabel.HorizontalAlignment = HorizontalAlignment.Center;
                nameLabel.AddThemeFontSizeOverride("font_size", 12);
                nameLabel.AddThemeColorOverride("font_color", borderColor);
                nameLabel.ClipText = true;
                nameLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
                hbox.AddChild(nameLabel);

                // 收藏星标
                bool isFavRelic = CardSpawnService.FavoriteRelicIds.Contains(rId);
                var starRelic = GodotExtensions._MakeStarButton(isFavRelic, CardSpawnService.FavoriteRelicIds, rId);
                hbox.AddChild(starRelic);

                _itemList.AddChild(cell);
            }
        };
    }

    private static string _RelicTooltip(RelicModel relic)
    {
        if (relic == null) return "";
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(relic.Title.GetFormattedText());
            var desc = SpawnCheatPanel._GetModelDesc(relic);
            if (!string.IsNullOrEmpty(desc))
            {
                desc = Regex.Replace(desc, @"\[/?\w+\]", "");
                desc = Regex.Replace(desc, @"\{[^}]*\}", "7");
                desc = desc.Trim();
                if (!string.IsNullOrEmpty(desc)) sb.AppendLine(desc);
            }
            return sb.ToString();
        }
        catch { return relic.Title?.GetFormattedText() ?? ""; }
    }

    private void _ToggleRelicMark(string relicId)
    {
        var list = CardSpawnService.MarkedRelicIds;
        bool wasMarked = list.Contains(relicId);
        if (wasMarked) list.Remove(relicId);
        else list.Add(relicId);
        CardSpawnService.RebuildRelicQueue();

        // 内联更新：只改这个 cell 的样式，不重建整个网格
        if (_itemList == null) return;
        var cell = _itemList.GetNodeOrNull<Panel>(relicId);
        if (cell == null) return;

        bool nowMarked = !wasMarked;
        var s = cell.GetThemeStylebox("panel") as StyleBoxFlat;
        if (s == null) return;

        var relic = ModelDb.AllRelics.FirstOrDefault(r => r.Id.Entry == relicId);
        var borderColor = relic != null ? _GetRarityColor(relic) : new Color(0.3f, 0.3f, 0.35f);

        s.BgColor = nowMarked
            ? new Color(0.12f, 0.25f, 0.12f, 0.8f)
            : new Color(0.08f, 0.08f, 0.12f, 0.8f);
        s.BorderColor = nowMarked ? new Color(1f, 0.85f, 0.3f) : borderColor;
        s.SetBorderWidthAll(nowMarked ? 2 : 1);
    }

    private static Color _GetRarityColor(RelicModel relic) => relic.Rarity switch
    {
        RelicRarity.Ancient => new Color(1f, 0.5f, 0.5f),
        RelicRarity.Rare => new Color(1f, 0.84f, 0f),
        RelicRarity.Uncommon => new Color(0.5f, 0.8f, 1f),
        _ => new Color(0.85f, 0.85f, 0.85f)
    };
}

/// <summary>药水生成 Tab</summary>
public partial class PotionTabContent : Control
{
    private const int GridCols = 5;
    private const int CellW = 140;
    private const int CellH = 48;

    private LineEdit? _searchField;
    private Control? _itemList;
    private Label? _countLabel;
    private int _potionRarityFilter = -1;

    public void Init()
    {
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var vbox = new VBoxContainer();
        vbox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        vbox.AddThemeConstantOverride("separation", 4);
        AddChild(vbox);

        var searchRow = new HBoxContainer();
        vbox.AddChild(searchRow);
        searchRow.AddChild(new Label { Text = I18n.T("search") });
        _searchField = new LineEdit();
        _searchField.PlaceholderText = I18n.T("search_potion");
        _searchField.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _searchField.TextChanged += _ => Refresh();
        searchRow.AddChild(_searchField);
        var rarityDropdown = new OptionButton();
        rarityDropdown.AddItem(I18n.T("filter_all"), -1);
        rarityDropdown.AddItem(I18n.T("rarity_common"), (int)PotionRarity.Common);
        rarityDropdown.AddItem(I18n.T("rarity_uncommon"), (int)PotionRarity.Uncommon);
        rarityDropdown.AddItem(I18n.T("rarity_rare"), (int)PotionRarity.Rare);
        rarityDropdown.AddItem(I18n.T("rarity_potion_event"), (int)PotionRarity.Event);
        rarityDropdown.AddItem(I18n.T("rarity_token"), (int)PotionRarity.Token);
        rarityDropdown.Selected = 0;
        rarityDropdown.CustomMinimumSize = new Vector2(58, 22);
        rarityDropdown.AddThemeFontSizeOverride("font_size", 11);
        rarityDropdown.ItemSelected += (idx) => { _potionRarityFilter = rarityDropdown.GetItemId((int)idx); Refresh(); };
        searchRow.AddChild(rarityDropdown);
        _countLabel = new Label { Text = "" };
        _countLabel.CustomMinimumSize = new Vector2(60, 0);
        _countLabel.HorizontalAlignment = HorizontalAlignment.Right;
        searchRow.AddChild(_countLabel);

        var scroll = new ScrollContainer();
        scroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        vbox.AddChild(scroll);

        _itemList = new Control();
        _itemList.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_itemList);

        Refresh();
    }

    public void Refresh()
    {
        if (_itemList == null) return;
        foreach (Node child in _itemList.GetChildren())
            child.QueueFree();

        string filter = _searchField?.Text.Trim().ToUpperInvariant() ?? "";
        var potions = ModelDb.AllPotions
            .Where(p => string.IsNullOrEmpty(filter) ||
                        p.Id.Entry.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(p => _potionRarityFilter < 0 || (int)p.Rarity == _potionRarityFilter)
            .ToList();

        _countLabel!.Text = string.Format(I18n.T("count_types"), potions.Count);

        // 和卡牌 Tab 一致：延时重建
        var tree = GetTree();
        if (tree == null) return;
        tree.CreateTimer(0.05f).Timeout += () =>
        {
            if (_itemList == null) return;
            int cols = GridCols;
            int cellW = CellW;
            int cellH = CellH;
            int gap = 4;
            // 设置最小尺寸让 ScrollContainer 可滚动
            int gridRows = (potions.Count + cols - 1) / cols;
            _itemList.CustomMinimumSize = new Vector2(0, gridRows * (cellH + gap));
            for (int i = 0; i < potions.Count; i++)
            {
                var potion = potions[i];
                string pId = potion.Id.Entry;
                bool isMarked = CardSpawnService.MarkedPotionIds.Contains(pId);

                int x = (i % cols) * (cellW + 4);
                int y = (i / cols) * (cellH + 4);

                var cell = new Panel();
                cell.Name = pId;
                cell.SetPosition(new Vector2(x, y));
                cell.SetSize(new Vector2(cellW, cellH));
                cell.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
                cell.TooltipText = _PotionTooltip(potion);

                var borderColor = _GetPotionRarityColor(potion);
                var s = new StyleBoxFlat();
                s.BgColor = isMarked ? new Color(0.12f, 0.25f, 0.12f, 0.8f) : new Color(0.08f, 0.08f, 0.12f, 0.8f);
                s.BorderColor = borderColor;
                s.SetBorderWidthAll(isMarked ? 2 : 1);
                s.CornerRadiusBottomLeft = 3;
                s.CornerRadiusBottomRight = 3;
                s.CornerRadiusTopLeft = 3;
                s.CornerRadiusTopRight = 3;
                cell.AddThemeStyleboxOverride("panel", s);

                var captured = pId;
                cell.GuiInput += (InputEvent ev) =>
                {
                    if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                        _TogglePotionMark(captured);
                };

                var hbox = new HBoxContainer();
                hbox.MouseFilter = Control.MouseFilterEnum.Ignore;
                hbox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                hbox.AddThemeConstantOverride("separation", 4);
                cell.AddChild(hbox);

                var nameLabel = new Label();
                nameLabel.Text = potion.Title.GetFormattedText();
                nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                nameLabel.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
                nameLabel.VerticalAlignment = VerticalAlignment.Center;
                nameLabel.HorizontalAlignment = HorizontalAlignment.Center;
                nameLabel.AddThemeFontSizeOverride("font_size", 12);
                nameLabel.AddThemeColorOverride("font_color", borderColor);
                nameLabel.ClipText = true;
                nameLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
                hbox.AddChild(nameLabel);

                // 收藏星标
                bool isFav = CardSpawnService.FavoritePotionIds.Contains(pId);
                var star = GodotExtensions._MakeStarButton(isFav, CardSpawnService.FavoritePotionIds, pId);
                hbox.AddChild(star);

                _itemList.AddChild(cell);
            }
        };
    }

    private static string _PotionTooltip(PotionModel potion)
    {
        if (potion == null) return "";
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(potion.Title.GetFormattedText());
            var desc = SpawnCheatPanel._GetModelDesc(potion);
            if (!string.IsNullOrEmpty(desc))
            {
                desc = Regex.Replace(desc, @"\[/?\w+\]", "");
                desc = Regex.Replace(desc, @"\{[^}]*\}", "7");
                desc = desc.Trim();
                if (!string.IsNullOrEmpty(desc)) sb.AppendLine(desc);
            }
            return sb.ToString();
        }
        catch { return potion.Title?.GetFormattedText() ?? ""; }
    }

    private void _TogglePotionMark(string potionId)
    {
        var list = CardSpawnService.MarkedPotionIds;
        bool wasMarked = list.Contains(potionId);
        if (wasMarked) list.Remove(potionId);
        else list.Add(potionId);
        CardSpawnService.RebuildPotionQueue();

        // 内联更新
        if (_itemList == null) return;
        var cell = _itemList.GetNodeOrNull<Panel>(potionId);
        if (cell == null) return;

        bool nowMarked = !wasMarked;
        var s = cell.GetThemeStylebox("panel") as StyleBoxFlat;
        if (s == null) return;

        var potion = ModelDb.AllPotions.FirstOrDefault(p => p.Id.Entry == potionId);
        var borderColor = potion != null ? _GetPotionRarityColor(potion) : new Color(0.3f, 0.3f, 0.35f);

        s.BgColor = nowMarked
            ? new Color(0.12f, 0.25f, 0.12f, 0.8f)
            : new Color(0.08f, 0.08f, 0.12f, 0.8f);
        s.BorderColor = nowMarked ? new Color(1f, 0.85f, 0.3f) : borderColor;
        s.SetBorderWidthAll(nowMarked ? 2 : 1);
    }

    private static Color _GetPotionRarityColor(PotionModel potion) => potion.Rarity switch
    {
        PotionRarity.Rare => new Color(1f, 0.84f, 0f),       // 金色
        PotionRarity.Uncommon => new Color(0.5f, 0.8f, 1f),  // 浅蓝
        PotionRarity.Event => new Color(0.9f, 0.5f, 0.9f),   // 紫
        PotionRarity.Token => new Color(0.6f, 0.6f, 0.6f),   // 灰
        _ => new Color(0.85f, 0.85f, 0.85f)                   // 白（Common/None）
    };
}

/// <summary>资源编辑 Tab — 金币/HP/能量/商店刷新</summary>
public partial class ResourceTabContent : VBoxContainer
{
    private SpinBox? _goldAmount;
    private SpinBox? _healAmount;
    private SpinBox? _energyAmount;

    public void Init()
    {
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 8);

        // 金币
        AddChild(new Label { Text = I18n.T("gold_section") });
        var goldRow = new HBoxContainer();
        _goldAmount = new SpinBox();
        _goldAmount.MinValue = -99999;
        _goldAmount.MaxValue = 99999;
        _goldAmount.Value = 100;
        _goldAmount.Step = 50;
        _goldAmount.CustomMinimumSize = new Vector2(120, 0);
        goldRow.AddChild(_goldAmount);

        var addGoldBtn = new Button { Text = I18n.T("add_gold") };
        addGoldBtn.Pressed += () => _ApplyGold((int)_goldAmount.Value);
        goldRow.AddChild(addGoldBtn);

        var setGoldBtn = new Button { Text = I18n.T("set_gold") };
        setGoldBtn.Pressed += () => _SetGold((int)_goldAmount.Value);
        goldRow.AddChild(setGoldBtn);
        AddChild(goldRow);

        AddChild(new HSeparator());

        // HP
        AddChild(new Label { Text = "─ HP ───────────────────────" });
        var healRow = new HBoxContainer();
        _healAmount = new SpinBox();
        _healAmount.MinValue = 1;
        _healAmount.MaxValue = 9999;
        _healAmount.Value = 50;
        _healAmount.Step = 10;
        _healAmount.CustomMinimumSize = new Vector2(100, 0);
        healRow.AddChild(_healAmount);

        var healBtn = new Button { Text = I18n.T("heal") };
        healBtn.Pressed += () => _ApplyHeal((int)_healAmount.Value);
        healRow.AddChild(healBtn);

        var fullHealBtn = new Button { Text = I18n.T("full_heal") };
        fullHealBtn.Pressed += _FullHeal;
        healRow.AddChild(fullHealBtn);
        AddChild(healRow);

        AddChild(new HSeparator());

        // 能量
        AddChild(new Label { Text = I18n.T("energy_section") });
        var energyRow = new HBoxContainer();
        _energyAmount = new SpinBox();
        _energyAmount.MinValue = 1;
        _energyAmount.MaxValue = 99;
        _energyAmount.Value = 3;
        _energyAmount.CustomMinimumSize = new Vector2(80, 0);
        energyRow.AddChild(_energyAmount);

        var addEnergyBtn = new Button { Text = I18n.T("add_energy") };
        addEnergyBtn.Pressed += () => _AddEnergy((int)_energyAmount.Value);
        energyRow.AddChild(addEnergyBtn);
        AddChild(energyRow);

        // ─── 商店刷新 ───
        AddChild(new HSeparator());
        AddChild(new Label { Text = I18n.T("shop_section") });
        var shopRow = new HBoxContainer();
        var refreshBtn = new Button { Text = "🔄 刷新商店" };
        refreshBtn.Pressed += () => ShopSpawnService.RefreshShop();
        shopRow.AddChild(refreshBtn);
        AddChild(shopRow);

    }

    public void Refresh() { }

    // ─── 金币 ────────────────────

    private static void _ApplyGold(int amount)
    {
        if (!RunManager.Instance.IsInProgress) return;
        Player? player = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
        if (player == null) return;
        TaskHelper.RunSafely(PlayerCmd.GainGold(amount, player));
    }

    private static void _SetGold(int target)
    {
        if (!RunManager.Instance.IsInProgress) return;
        Player? player = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
        if (player == null) return;
        int current = player.Gold;
        int delta = target - current;
        TaskHelper.RunSafely(PlayerCmd.GainGold(delta, player));
    }

    // ─── HP ──────────────────────

    private static void _ApplyHeal(int amount)
    {
        if (!RunManager.Instance.IsInProgress) return;
        Player? player = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
        if (player?.Creature == null) return;
        TaskHelper.RunSafely(CreatureCmd.Heal(player.Creature, amount));
    }

    private static void _FullHeal()
    {
        if (!RunManager.Instance.IsInProgress) return;
        Player? player = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
        if (player?.Creature == null) return;
        int missing = player.Creature.MaxHp - player.Creature.CurrentHp;
        if (missing <= 0) return;
        TaskHelper.RunSafely(CreatureCmd.Heal(player.Creature, missing));
    }

    // ─── 能量 ────────────────────

    private static void _AddEnergy(int amount)
    {
        if (!RunManager.Instance.IsInProgress) return;
        Player? player = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
        if (player == null) return;
        TaskHelper.RunSafely(PlayerCmd.GainEnergy(amount, player));
    }
}

/// <summary>收藏 Tab — 汇总收藏列表（上）+ 收藏集（下）</summary>
public partial class FavoriteTabContent : Control
{
    private VBoxContainer? _cardCol, _relicCol, _potionCol;
    private Label? _countLabel;
    private VBoxContainer? _collectionPanel;
    private LineEdit? _collectionNameInput;
    private CheckBox? _appendModeToggle;

    public void Init()
    {
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var vbox = new VBoxContainer();
        vbox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        vbox.AddThemeConstantOverride("separation", 4);
        AddChild(vbox);

        _countLabel = new Label();
        _countLabel.AddThemeFontSizeOverride("font_size", 11);
        _countLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.6f));
        vbox.AddChild(_countLabel);

        var favScroll = new ScrollContainer();
        favScroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        favScroll.CustomMinimumSize = new Vector2(0, 200);
        vbox.AddChild(favScroll);

        var hbox = new HBoxContainer();
        hbox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        favScroll.AddChild(hbox);
        _cardCol = new VBoxContainer(); _cardCol.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; _cardCol.SizeFlagsVertical = Control.SizeFlags.ExpandFill; hbox.AddChild(_cardCol);
        hbox.AddChild(new VSeparator());
        _relicCol = new VBoxContainer(); _relicCol.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; _relicCol.SizeFlagsVertical = Control.SizeFlags.ExpandFill; hbox.AddChild(_relicCol);
        hbox.AddChild(new VSeparator());
        _potionCol = new VBoxContainer(); _potionCol.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; _potionCol.SizeFlagsVertical = Control.SizeFlags.ExpandFill; hbox.AddChild(_potionCol);

        vbox.AddChild(new HSeparator());
        var setRow = new HBoxContainer();
        _collectionNameInput = new LineEdit();
        _collectionNameInput.PlaceholderText = I18n.T("collection_name_hint");
        _collectionNameInput.CustomMinimumSize = new Vector2(140, 22);
        _collectionNameInput.AddThemeFontSizeOverride("font_size", 11);
        setRow.AddChild(_collectionNameInput);
        var saveBtn = new Button(); saveBtn.Text = I18n.T("save_current"); saveBtn.CustomMinimumSize = new Vector2(80, 22); saveBtn.AddThemeFontSizeOverride("font_size", 11);
        saveBtn.Pressed += () => { CardSpawnService.SaveCurrentAsCollection(_collectionNameInput!.Text); _collectionNameInput.Text = ""; Refresh(); };
        setRow.AddChild(saveBtn);
        _appendModeToggle = new CheckBox();
        _appendModeToggle.Text = I18n.T("append_mode");
        _appendModeToggle.AddThemeFontSizeOverride("font_size", 10);
        _appendModeToggle.ButtonPressed = false;
        setRow.AddChild(_appendModeToggle);
        vbox.AddChild(setRow);

        _collectionPanel = new VBoxContainer();
        _collectionPanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _collectionPanel.AddThemeConstantOverride("separation", 2);
        vbox.AddChild(_collectionPanel);
    }

    private void _BuildFavItemRow(Control container, string name, bool isMarked, Action toggle, Action remove)
    {
        var row = new HBoxContainer(); row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var lbl = new Label(); lbl.Text = name; lbl.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin; lbl.AddThemeFontSizeOverride("font_size", 11);
        if (isMarked) lbl.AddThemeColorOverride("font_color", new Color(0.3f, 1f, 0.3f)); row.AddChild(lbl);
        var markBtn = new Button(); markBtn.Text = isMarked ? "−" : "+"; markBtn.CustomMinimumSize = new Vector2(24, 18); markBtn.AddThemeFontSizeOverride("font_size", 10);
        if (isMarked) markBtn.AddThemeColorOverride("font_color", new Color(0.3f, 1f, 0.3f));
        markBtn.Pressed += () => { toggle(); Refresh(); }; row.AddChild(markBtn);
        var delBtn = new Button(); delBtn.Text = I18n.T("delete"); delBtn.CustomMinimumSize = new Vector2(28, 18); delBtn.AddThemeFontSizeOverride("font_size", 10); delBtn.AddThemeColorOverride("font_color", new Color(0.8f, 0.2f, 0.2f));
        delBtn.Pressed += () => { remove(); Refresh(); }; row.AddChild(delBtn);
        container.AddChild(row);
    }

    public void Refresh()
    {
        CardSpawnService.LoadFavorites();
        _cardCol?.ClearChildren(); _relicCol?.ClearChildren(); _potionCol?.ClearChildren();
        if (_cardCol == null || _relicCol == null || _potionCol == null) return;
        int total = 0;

        var clearRow = new HBoxContainer(); clearRow.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; clearRow.AddThemeConstantOverride("separation", 4);
        Button MakeClear(string t, Action a) { var b = new Button(); b.Text = t; b.CustomMinimumSize = new Vector2(0, 20); b.AddThemeFontSizeOverride("font_size", 10); b.AddThemeColorOverride("font_color", new Color(0.7f, 0.3f, 0.3f)); b.Pressed += () => { a(); Refresh(); }; return b; }
        clearRow.AddChild(MakeClear(I18n.T("clear_all"), () => { CardSpawnService.FavoriteCardIds.Clear(); CardSpawnService.FavoriteRelicIds.Clear(); CardSpawnService.FavoritePotionIds.Clear(); CardSpawnService.SaveFavorites(); }));
        clearRow.AddChild(MakeClear(I18n.T("clear_cards"), () => { CardSpawnService.FavoriteCardIds.Clear(); CardSpawnService.SaveFavorites(); }));
        clearRow.AddChild(MakeClear(I18n.T("clear_relics"), () => { CardSpawnService.FavoriteRelicIds.Clear(); CardSpawnService.SaveFavorites(); }));
        clearRow.AddChild(MakeClear(I18n.T("clear_potions"), () => { CardSpawnService.FavoritePotionIds.Clear(); CardSpawnService.SaveFavorites(); }));
        _cardCol.AddChild(clearRow);

        var favCards = CardSpawnService.FavoriteCardIds;
        if (favCards.Count > 0)
        {
            var h = new Label(); h.Text = string.Format(I18n.T("fav_header_cards"), favCards.Count); h.AddThemeFontSizeOverride("font_size", 12); h.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.2f)); _cardCol.AddChild(h);
            total += favCards.Count;
            foreach (var cId in favCards)
            {
                var card = CardSpawnService.FindCard(cId);
                if (card == null) continue;
                var captured = cId; bool marked = CardSpawnService.SelectedCardIdsForReward.Contains(cId);
                _BuildFavItemRow(_cardCol, card.Title, marked, () => { var l = CardSpawnService.SelectedCardIdsForReward; if (l.Contains(captured)) l.Remove(captured); else l.Add(captured); CardSpawnService.RebuildReplacementQueue(); }, () => { CardSpawnService.FavoriteCardIds.Remove(captured); CardSpawnService.SaveFavorites(); });
            }
        }
        var favRelics = CardSpawnService.FavoriteRelicIds;
        if (favRelics.Count > 0)
        {
            var h = new Label(); h.Text = string.Format(I18n.T("fav_header_relics"), favRelics.Count); h.AddThemeFontSizeOverride("font_size", 12); h.AddThemeColorOverride("font_color", new Color(0.2f, 0.7f, 0.7f)); _relicCol.AddChild(h);
            total += favRelics.Count;
            foreach (var rId in favRelics)
            {
                var relic = ModelDb.AllRelics.FirstOrDefault(r => r.Id.Entry == rId);
                if (relic == null) continue;
                var captured = rId; bool marked = CardSpawnService.MarkedRelicIds.Contains(rId);
                _BuildFavItemRow(_relicCol, relic.Title.GetFormattedText(), marked, () => { var l = CardSpawnService.MarkedRelicIds; if (l.Contains(captured)) l.Remove(captured); else l.Add(captured); CardSpawnService.RebuildRelicQueue(); }, () => { CardSpawnService.FavoriteRelicIds.Remove(captured); CardSpawnService.SaveFavorites(); });
            }
        }
        var favPotions = CardSpawnService.FavoritePotionIds;
        if (favPotions.Count > 0)
        {
            var h = new Label(); h.Text = string.Format(I18n.T("fav_header_potions"), favPotions.Count); h.AddThemeFontSizeOverride("font_size", 12); h.AddThemeColorOverride("font_color", new Color(0.7f, 0.2f, 0.7f)); _potionCol.AddChild(h);
            total += favPotions.Count;
            foreach (var pId in favPotions)
            {
                var potion = ModelDb.AllPotions.FirstOrDefault(p => p.Id.Entry == pId);
                if (potion == null) continue;
                var captured = pId; bool marked = CardSpawnService.MarkedPotionIds.Contains(pId);
                _BuildFavItemRow(_potionCol, potion.Title.GetFormattedText(), marked, () => { var l = CardSpawnService.MarkedPotionIds; if (l.Contains(captured)) l.Remove(captured); else l.Add(captured); CardSpawnService.RebuildPotionQueue(); }, () => { CardSpawnService.FavoritePotionIds.Remove(captured); CardSpawnService.SaveFavorites(); });
            }
        }
        _countLabel!.Text = total == 0 ? I18n.T("fav_empty") : string.Format(I18n.T("fav_total"), total);

        if (_collectionPanel == null) return;
        _collectionPanel.ClearChildren();
        var sets = CardSpawnService.Collections;
        if (sets.Count == 0)
        {
            var e = new Label(); e.Text = I18n.T("collection_empty"); e.AddThemeFontSizeOverride("font_size", 11); e.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f)); _collectionPanel.AddChild(e);
            return;
        }
        int setIdx = 0;
        foreach (var set in sets)
        {
            var row = new HBoxContainer(); row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            var n = new Label(); n.Text = $"{set.Name} ({set.Cards.Count}C {set.Relics.Count}R {set.Potions.Count}P)"; n.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; n.AddThemeFontSizeOverride("font_size", 11); row.AddChild(n);
            var idx = setIdx;
            var ap = new Button(); ap.Text = I18n.T("append"); ap.CustomMinimumSize = new Vector2(40, 20); ap.AddThemeFontSizeOverride("font_size", 10);
            ap.Pressed += () =>
            {
                if (_appendModeToggle != null && _appendModeToggle.ButtonPressed)
                {
                    CardSpawnService.CurrentMarksToCollection(idx);
                }
                else
                {
                    CardSpawnService.AppendCollection(idx);
                }
                Refresh();
            };
            row.AddChild(ap);
            var del = new Button(); del.Text = "×"; del.CustomMinimumSize = new Vector2(24, 20); del.AddThemeFontSizeOverride("font_size", 10); del.AddThemeColorOverride("font_color", new Color(0.8f, 0.2f, 0.2f));
            del.Pressed += () => { CardSpawnService.RemoveCollection(idx); Refresh(); };
            row.AddChild(del);
            _collectionPanel.AddChild(row);
            setIdx++;
        }
    }
}
