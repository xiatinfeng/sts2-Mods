using Godot;
using System.Collections.Generic;
using System.Linq;
using MapOddsTracker.Scripts;

namespace MapOddsTracker.Scripts.UI;

/// <summary>
/// 遭遇队列覆盖层：只显示消耗队列，支持ACT切换、筛选、快捷键、拖拽和缩放
/// </summary>
public partial class MapOverlay : Control
{
    private static readonly Key ToggleKey = Key.J;

    private PanelContainer _mainPanel = null!;
    private VBoxContainer _content = null!;
    private HBoxContainer _actTabs = null!;
    private HBoxContainer _filterTabs = null!;
    private Label _titleLabel = null!;
    private Button _dragHandle = null!;
    private ScrollContainer _scrollContainer = null!;
    private VBoxContainer _nodeList = null!;
    private HSlider _zoomSlider = null!;
    private MonsterTooltip _tooltip = null!;

    private int _selectedAct = 1;
    private FilterMode _filterMode = FilterMode.All;
    private bool _isVisible = false;
    private float _savedScrollPosition = 0f;
    private float _zoomLevel = 1.0f;

    // 拖拽状态
    private bool _isDragging = false;
    private Vector2 _dragOffset;

    private enum FilterMode { All, Normal, Elite }

    public override void _Ready()
    {
        // 主面板
        _mainPanel = new PanelContainer();
        _mainPanel.CustomMinimumSize = new Vector2(340, 480);
        AddChild(_mainPanel);

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.08f, 0.12f, 0.92f),
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10,
            ContentMarginLeft = 12,
            ContentMarginTop = 12,
            ContentMarginRight = 12,
            ContentMarginBottom = 12
        };
        _mainPanel.AddThemeStyleboxOverride("panel", style);

        _content = new VBoxContainer();
        _content.AddThemeConstantOverride("separation", 6);
        _mainPanel.AddChild(_content);

        // 标题行（含拖拽按钮和缩放）
        var titleRow = new HBoxContainer();
        titleRow.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
        _content.AddChild(titleRow);

        // 拖拽手柄
        _dragHandle = new Button();
        _dragHandle.Text = "☰";
        _dragHandle.TooltipText = "拖拽移动";
        _dragHandle.CustomMinimumSize = new Vector2(28, 28);
        _dragHandle.AddThemeFontSizeOverride("font_size", 14);
        _dragHandle.MouseDefaultCursorShape = CursorShape.Drag;
        titleRow.AddChild(_dragHandle);

        // 标题
        _titleLabel = new Label();
        _titleLabel.Text = "遭遇队列 (按 J 切换)";
        _titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _titleLabel.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
        _titleLabel.AddThemeFontSizeOverride("font_size", 15);
        titleRow.AddChild(_titleLabel);

        // 缩放滑块
        var zoomContainer = new VBoxContainer();
        zoomContainer.CustomMinimumSize = new Vector2(70, 0);
        zoomContainer.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        titleRow.AddChild(zoomContainer);

        var zoomLabel = new Label();
        zoomLabel.Text = "🔍";
        zoomLabel.HorizontalAlignment = HorizontalAlignment.Center;
        zoomLabel.AddThemeFontSizeOverride("font_size", 12);
        zoomContainer.AddChild(zoomLabel);

        _zoomSlider = new HSlider();
        _zoomSlider.MinValue = 0.7;
        _zoomSlider.MaxValue = 1.5;
        _zoomSlider.Step = 0.1;
        _zoomSlider.Value = 1.0;
        _zoomSlider.CustomMinimumSize = new Vector2(60, 16);
        _zoomSlider.ValueChanged += OnZoomChanged;
        zoomContainer.AddChild(_zoomSlider);

        // ACT 标签
        _actTabs = new HBoxContainer();
        _actTabs.Alignment = BoxContainer.AlignmentMode.Center;
        _actTabs.AddThemeConstantOverride("separation", 6);
        _content.AddChild(_actTabs);

        // 筛选标签
        _filterTabs = new HBoxContainer();
        _filterTabs.Alignment = BoxContainer.AlignmentMode.Center;
        _filterTabs.AddThemeConstantOverride("separation", 6);
        _content.AddChild(_filterTabs);

        // 列表区域（可滚动）
        _scrollContainer = new ScrollContainer();
        _scrollContainer.SizeFlagsVertical = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
        _content.AddChild(_scrollContainer);

        _nodeList = new VBoxContainer();
        _nodeList.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
        _nodeList.AddThemeConstantOverride("separation", 4);
        _scrollContainer.AddChild(_nodeList);

        // 锚点：右上角
        AnchorLeft = 1.0f;
        AnchorTop = 0.0f;
        AnchorRight = 1.0f;
        AnchorBottom = 0.0f;
        OffsetLeft = -365;
        OffsetTop = 10;
        OffsetRight = -15;
        OffsetBottom = 500;

        // Tooltip
        _tooltip = new MonsterTooltip();
        AddChild(_tooltip);

        // 初始隐藏
        _mainPanel.Hide();
        _isVisible = false;
        Visible = false;

        RefreshActTabs();
        RefreshFilterTabs();
        UpdateDisplay();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            if (keyEvent.Keycode == ToggleKey)
            {
                ToggleVisibility();
                GetViewport()?.SetInputAsHandled();
            }
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseButton)
        {
            if (mouseButton.ButtonIndex == MouseButton.Left)
            {
                if (mouseButton.Pressed)
                {
                    if (_dragHandle.GetGlobalRect().HasPoint(mouseButton.GlobalPosition))
                    {
                        _isDragging = true;
                        _dragOffset = mouseButton.GlobalPosition - GlobalPosition;
                    }
                }
                else
                {
                    _isDragging = false;
                }
            }
        }
        else if (@event is InputEventMouseMotion mouseMotion && _isDragging)
        {
            GlobalPosition = mouseMotion.GlobalPosition - _dragOffset;
        }
    }

    private void OnZoomChanged(double value)
    {
        _zoomLevel = (float)value;
        UpdateDisplay();
    }

    public void ToggleVisibility()
    {
        if (_isVisible)
        {
            _savedScrollPosition = _scrollContainer.ScrollVertical;
            _isVisible = false;
            _mainPanel.Hide();
            _tooltip?.HideTooltip();
            // Completely hide the overlay so it doesn't block mouse input
            Visible = false;
        }
        else
        {
            _isVisible = true;
            Visible = true;
            _mainPanel.Show();
            MapTracker.RefreshConsumptionState();
            RefreshActTabs();
            RefreshFilterTabs();
            UpdateDisplay();
            CallDeferred(nameof(RestoreScrollPosition));
        }
    }

    private void RestoreScrollPosition()
    {
        _scrollContainer.ScrollVertical = (int)_savedScrollPosition;
    }

    public void ShowPanel()
    {
        _isVisible = true;
        Visible = true;
        _mainPanel.Show();
        MapTracker.RefreshConsumptionState();
        RefreshActTabs();
        RefreshFilterTabs();
        UpdateDisplay();
        CallDeferred(nameof(RestoreScrollPosition));
    }

    private void RefreshActTabs()
    {
        foreach (var child in _actTabs.GetChildren())
            child.QueueFree();

        var acts = MapTracker.GetAllActNumbers();
        if (acts.Count == 0)
        {
            var hint = new Label();
            hint.Text = "(进入地图后显示)";
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.Modulate = new Color(0.5f, 0.5f, 0.5f);
            hint.AddThemeFontSizeOverride("font_size", ScaleFont(11));
            _actTabs.AddChild(hint);
            return;
        }

        foreach (int actNum in acts)
        {
            var btn = new Button();
            btn.Text = $"ACT{actNum}";
            btn.CustomMinimumSize = new Vector2(50, 28);
            btn.AddThemeFontSizeOverride("font_size", ScaleFont(12));

            int captured = actNum;
            btn.Pressed += () => OnActTabClicked(captured);

            if (captured == _selectedAct)
                btn.ButtonPressed = true;

            _actTabs.AddChild(btn);
        }
    }

    private void RefreshFilterTabs()
    {
        foreach (var child in _filterTabs.GetChildren())
            child.QueueFree();

        var modes = new[] { FilterMode.All, FilterMode.Normal, FilterMode.Elite };
        var labels = new[] { "全部", "⚔ 普通", "◆ 精英" };

        for (int i = 0; i < modes.Length; i++)
        {
            var btn = new Button();
            btn.Text = labels[i];
            btn.CustomMinimumSize = new Vector2(60, 26);
            btn.AddThemeFontSizeOverride("font_size", ScaleFont(11));

            var mode = modes[i];
            btn.Pressed += () => OnFilterClicked(mode);

            if (mode == _filterMode)
                btn.ButtonPressed = true;

            _filterTabs.AddChild(btn);
        }
    }

    private void OnActTabClicked(int act)
    {
        _selectedAct = act;
        RefreshActTabs();
        UpdateDisplay();
    }

    private void OnFilterClicked(FilterMode mode)
    {
        _filterMode = mode;
        RefreshFilterTabs();
        UpdateDisplay();
    }

    public void OnGameActChanged(int gameActNumber)
    {
        _selectedAct = gameActNumber;
        if (_isVisible)
        {
            RefreshActTabs();
            UpdateDisplay();
        }
    }

    private int ScaleFont(int baseSize)
    {
        return Mathf.Max(8, (int)(baseSize * _zoomLevel));
    }

    private void UpdateDisplay()
    {
        foreach (var child in _nodeList.GetChildren())
            child.QueueFree();

        var normalQueue = MapTracker.GetEncounterQueue(_selectedAct, NodeType.Monster);
        var eliteQueue = MapTracker.GetEncounterQueue(_selectedAct, NodeType.Elite);

        bool showNormal = _filterMode == FilterMode.All || _filterMode == FilterMode.Normal;
        bool showElite = _filterMode == FilterMode.All || _filterMode == FilterMode.Elite;

        if (normalQueue.Count == 0 && eliteQueue.Count == 0)
        {
            var empty = CreateLabel("(无数据 - 请开始一局新游戏)", new Color(0.5f, 0.5f, 0.5f), 12);
            _nodeList.AddChild(empty);
            return;
        }

        // 提示文字
        var hint = CreateLabel("灰色=已遭遇 | 白色=未遭遇", new Color(0.5f, 0.5f, 0.5f), 9);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        _nodeList.AddChild(hint);

        // 普通怪物队列
        if (showNormal && normalQueue.Count > 0)
        {
            var monsterTitle = CreateLabel("⚔ 普通怪物", Colors.White, 11);
            monsterTitle.HorizontalAlignment = HorizontalAlignment.Center;
            _nodeList.AddChild(monsterTitle);

            var monsterContainer = new HFlowContainer();
            monsterContainer.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
            _nodeList.AddChild(monsterContainer);

            foreach (var item in normalQueue)
            {
                var label = CreateQueueLabel(item);
                monsterContainer.AddChild(label);
            }
        }

        // 精英怪物队列
        if (showElite && eliteQueue.Count > 0)
        {
            var eliteTitle = CreateLabel("◆ 精英怪物", new Color(1f, 0.6f, 0f), 11);
            eliteTitle.HorizontalAlignment = HorizontalAlignment.Center;
            _nodeList.AddChild(eliteTitle);

            var eliteContainer = new HFlowContainer();
            eliteContainer.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
            _nodeList.AddChild(eliteContainer);

            foreach (var item in eliteQueue)
            {
                var label = CreateQueueLabel(item);
                eliteContainer.AddChild(label);
            }
        }

        // Boss 信息（固定展示，不受筛选影响）
        var bossInfo = MapTracker.GetBossInfo(_selectedAct);
        if (bossInfo != null)
        {
            var bossSeparator = new HSeparator();
            bossSeparator.Modulate = new Color(0.3f, 0.3f, 0.3f);
            _nodeList.AddChild(bossSeparator);

            var bossTitle = CreateLabel("👑 Boss", new Color(1f, 0.2f, 0.2f), 12);
            bossTitle.HorizontalAlignment = HorizontalAlignment.Center;
            _nodeList.AddChild(bossTitle);

            var bossLabel = CreateQueueLabel(bossInfo);
            bossLabel.HorizontalAlignment = HorizontalAlignment.Center;
            bossLabel.AddThemeFontSizeOverride("font_size", ScaleFont(10));
            _nodeList.AddChild(bossLabel);
        }
    }

    private Label CreateQueueLabel(EncounterQueueItem item)
    {
        var label = new Label();
        label.Text = $"{item.Index}.{item.Name}";
        label.AddThemeFontSizeOverride("font_size", ScaleFont(9));
        label.MouseFilter = MouseFilterEnum.Stop;

        if (item.IsConsumed)
        {
            label.Modulate = new Color(0.4f, 0.4f, 0.4f);
        }
        else
        {
            label.Modulate = item.Type == NodeType.Elite
                ? new Color(1f, 0.7f, 0.3f)
                : new Color(0.9f, 0.9f, 0.9f);
        }

        // Mouse hover for tooltip - capture Name, Id, MonsterIds, and Type in closure
        string displayName = item.Name;
        string englishId = item.Id;
        var monsterIds = item.MonsterIds;
        bool isBoss = item.Type == NodeType.Boss;
        label.MouseEntered += () => OnMonsterHover(displayName, englishId, monsterIds, isBoss);
        label.MouseExited += OnMonsterHoverExit;

        return label;
    }

    private void OnMonsterHover(string displayName, string englishId, List<string> monsterIds, bool isBoss)
    {
        if (_tooltip == null) return;
        var mousePos = GetGlobalMousePosition();
        _tooltip.ShowMonster(displayName, englishId, monsterIds, mousePos, isBoss);
    }

    private void OnMonsterHoverExit()
    {
        _tooltip?.HideTooltip();
    }

    private static Label CreateLabel(string text, Color color, int fontSize)
    {
        var label = new Label();
        label.Text = text;
        label.Modulate = color;
        label.AddThemeFontSizeOverride("font_size", fontSize);
        return label;
    }
}
