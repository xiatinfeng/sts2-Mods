using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using MegaCrit.Sts2.Core.Modding;
using ModSyncChecker.Scripts;

namespace ModSyncChecker.Scripts.UI;

/// <summary>
/// MOD 差异显示面板（v2.10.1 — 仅中码(gzip+Base64)，应用排序改为地图场景检测，长码已删除）
/// </summary>
public partial class ModSyncPanel : Control
{
    private Control _mainPanel = null!;
    private VBoxContainer _content = null!;
    private VBoxContainer _diffList = null!;
    private Label _summaryLabel = null!;
    private RichTextLabel _hintLabel = null!;
    private Button _exportBtn = null!;
    private Button _closeBtn = null!;
    private Button _refreshBtn = null!;
    private Button _importBtn = null!;
    private Button _applyOrderBtn = null!;
    private Button _copyDiffBtn = null!;
    private Button _deleteProfileBtn = null!;
    private OptionButton _profileSelector = null!;
    private LineEdit _searchLineEdit = null!;
    private FileDialog? _fileDialog;
    private ConfirmationDialog? _deleteConfirmDialog;
    private ConfirmationDialog? _applyOrderConfirmDialog; // v2.6.0
    private List<string> _currentProfiles = new();
    private ModProfile? _currentProfile;
    private string? _selectedProfilePath; // v2.1.2: 精确追踪选中配置文件完整路径，替代 Contains 子串匹配
    private List<ModDifference>? _currentDiffData; // v2.2.0: 存储当前差异数据用于一键复制
    private List<ModInfoSnapshot>? _currentLocalModsData; // v2.2.0: 存储当前本地MOD数据用于一键复制

    private bool _isVisible = false;
    private bool _isCodeMode = false; // v2.0.0: 面板模式记忆 — false=导入导出模式, true=编码模式

    // v2.7.0: config.json settings
    private string _defaultPanel = "encoding";
    public static float FontScale { get; private set; } = 1.0f;

    // v2.7.0: Apply config font_scale to base font size
    private static int ScaledFontSize(int baseSize) => Mathf.RoundToInt(baseSize * FontScale);

    // v2.1.0: 图层切换
    private Button _modeToggleBtn = null!;
    private VBoxContainer _codeLayer = null!;
    private PasteDetectTextEdit _codeInput = null!;
    private Button _genCodeBtn = null!;
    private Button _compareCodeBtn = null!;
    private Label _codeResultLabel = null!;
    private Label _codeHintLabel = null!; // v2.10.7: field for RebuildStaticUI

    // v2.10.0: 编码排序应用按钮
    private Button _applyEncodingOrderBtn = null!;
    private ConfirmationDialog? _applyEncodingOrderConfirmDialog; // v2.10.0

    // v2.10.5: 编码历史缓存 — CodeHistoryManager 持久化到 code_history.json
    private CodeHistoryManager _codeHistory = null!;
    private OptionButton _codeHistoryDropdown = null!;

    // 语言切换检测
    private string _lastLocale = "";
    private int _langCheckFrameCounter; // v2.4.1: throttled language change detection (300 frames ~5s)

    // 需要动态刷新的静态标签（原 _Ready 中的局部变量）
    private Label _titleLabel = null!;
    private Label _profileLabel = null!;

    // ===== v1.2.0 可移动窗口系统 =====
    private VBoxContainer _windowRoot = null!;
    private Control _titleBar = null!;
    private MarginContainer _contentMargin = null!;
    private Control _resizeHandle = null!;
    private Button _minimizeBtn = null!;
    private Button _maximizeBtn = null!;

    private bool _isDragging = false;
    private bool _isResizing = false;
    private bool _buttonClickFrame = false; // 标记按钮点击帧，防止拖拽误触发
    private Vector2 _dragStartMouse;
    private Vector2 _dragStartOffset;
    private Vector2 _resizeStartMouse;
    private Vector2 _resizeStartSize;

    private bool _isMaximized = false;
    private bool _isMinimized = false;
    private Vector2 _normalPosition;
    private Vector2 _normalSize;

    // 当前实际尺寸的权威缓存，绕过 Godot Size 属性的一帧延迟
    private Vector2 _currentSize;

    // Y=350 确保面板缩小到底时 ScrollContainer 仍有 ~100px 可用空间，不会内容溢出
    private static readonly Vector2 MinWindowSize = new Vector2(450, 350);
    private const float TitleBarHeight = 36f;

    // ===== v1.2.1 窗口状态持久化 =====
    private WindowStateManager _windowState = null!;

    // 颜色定义
    private static readonly Color ColorMissingLocal = new(1f, 0.3f, 0.3f);    // 红色：本地缺失
    private static readonly Color ColorMissingRemote = new(1f, 0.7f, 0.2f);   // 橙色：远程缺失
    private static readonly Color ColorVersionMismatch = new(1f, 0.9f, 0.2f); // 黄色：版本不一致
    private static readonly Color ColorStateMismatch = new(0.8f, 0.4f, 1f);   // 紫色：状态不一致
    private static readonly Color ColorHashMismatch = new(0.3f, 0.7f, 1f);    // 蓝色：哈希不一致
    private static readonly Color ColorOrderMismatch = new(0.3f, 1f, 0.7f);   // 青色：顺序不一致
    private static readonly Color ColorExtraMod = new(0.5f, 0.5f, 0.5f);      // 灰色：额外MOD
    private static readonly Color ColorNormal = new(0.9f, 0.9f, 0.9f);
    private static readonly Color ColorHeader = new(1f, 1f, 1f);

    public override void _Ready()
    {
        // 确保即使游戏暂停也能处理输入
        ProcessMode = ProcessModeEnum.Always;

        // 初始化窗口状态管理器
        _windowState = new WindowStateManager();

        // v2.7.0: 加载 config.json（font_scale, default_panel）
        var config = ModSyncCore.Config;
        FontScale = config.FontScale;
        _defaultPanel = config.DefaultPanel;
        _isCodeMode = _defaultPanel == "encoding";

        // UI 构建拆分为独立方法
        CreateMainPanel();
        CreateTitleBar();
        CreateMainContentArea();
        CreateCodeLayer();
        CreateResizeHandle();

        // 底部提示文字改为根节点直接子节点，手动定位，
        // 避免被 VBoxContainer 空间竞争挤出面板边界
        AddChild(_hintLabel);

        // ========== 应用持久化的窗口状态 ==========
        bool hasSavedState = ApplyWindowState();

        // v2.1.0: 应用图层模式
        ApplyLayerMode();

        // 首次运行或未保存状态时，设置默认位置居中并初始化 normalPosition/normalSize
        if (!hasSavedState)
        {
            var viewport = GetViewportRect().Size;
            // Mobile: fill screen with margin; Desktop: centered fixed size
            float defaultW = PlatformHelper.IsMobile ? Mathf.Max(viewport.X - 16f, 400f) : 650f;
            float defaultH = PlatformHelper.IsMobile ? Mathf.Max(viewport.Y - 16f, 300f) : 520f;
            float defaultX = (viewport.X - defaultW) / 2f;
            float defaultY = (viewport.Y - defaultH) / 2f;

            AnchorLeft = AnchorTop = AnchorRight = AnchorBottom = 0;
            OffsetLeft = defaultX;
            OffsetTop = defaultY;
            OffsetRight = defaultX + defaultW;
            OffsetBottom = defaultY + defaultH;
        }

        // 确保 _normalPosition / _normalSize 始终有效（防止 Vector2.Zero 导致最大化还原到左上角）
        if (_normalPosition == Vector2.Zero)
        {
            _normalPosition = new Vector2(OffsetLeft, OffsetTop);
            _normalSize = new Vector2(OffsetRight - OffsetLeft, OffsetBottom - OffsetTop);
        }

        HidePanel();
        RefreshProfileList();

        // v2.1.2: 恢复上次选择的配置文件
        var savedState = _windowState.Load();
        if (savedState?.LastProfilePath != null && File.Exists(savedState.LastProfilePath))
        {
            OnProfileSelectedByPath(savedState.LastProfilePath);
        }
    }

    private void CreateMainPanel()
    {
        _mainPanel = new PanelBackground();
        _mainPanel.Position = Vector2.Zero;
        AddChild(_mainPanel);

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.06f, 0.1f, 0.95f),
            CornerRadiusTopLeft = 12,
            CornerRadiusTopRight = 12,
            CornerRadiusBottomLeft = 12,
            CornerRadiusBottomRight = 12,
            ContentMarginLeft = 0,
            ContentMarginTop = 0,
            ContentMarginRight = 0,
            ContentMarginBottom = 0
        };
        ((PanelBackground)_mainPanel).SetBackgroundStyle(style);

        _windowRoot = new VBoxContainer();
        _windowRoot.AddThemeConstantOverride("separation", 0);
        _mainPanel.AddChild(_windowRoot);
    }

    private void CreateTitleBar()
    {
        _titleBar = new PanelContainer();
        _titleBar.CustomMinimumSize = new Vector2(0, TitleBarHeight);
        _titleBar.MouseFilter = MouseFilterEnum.Pass;
        var titleBarStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.1f, 0.1f, 0.15f, 1f),
            CornerRadiusTopLeft = 12,
            CornerRadiusTopRight = 12,
            CornerRadiusBottomLeft = 0,
            CornerRadiusBottomRight = 0,
            ContentMarginLeft = 12,
            ContentMarginRight = 8,
            ContentMarginTop = 4,
            ContentMarginBottom = 4
        };
        _titleBar.AddThemeStyleboxOverride("panel", titleBarStyle);
        _windowRoot.AddChild(_titleBar);

        var titleRow = new HBoxContainer();
        titleRow.Alignment = BoxContainer.AlignmentMode.Center;
        titleRow.AddThemeConstantOverride("separation", 6);
        titleRow.MouseFilter = MouseFilterEnum.Pass;
        _titleBar.AddChild(titleRow);

        _titleLabel = new Label();
        _titleLabel.Text = L.T("Title");
        _titleLabel.HorizontalAlignment = HorizontalAlignment.Left;
        _titleLabel.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
        _titleLabel.AddThemeFontSizeOverride("font_size", ScaledFontSize(14));
        _titleLabel.Modulate = ColorHeader;
        _titleLabel.MouseFilter = MouseFilterEnum.Pass;
        titleRow.AddChild(_titleLabel);

        _minimizeBtn = CreateWindowButton("—", OnMinimizeClicked, new Color(0.9f, 0.7f, 0.2f));
        _maximizeBtn = CreateWindowButton("□", OnMaximizeClicked, new Color(0.3f, 0.8f, 0.4f));
        _closeBtn = CreateWindowButton("×", OnCloseClicked, new Color(1f, 0.3f, 0.3f));
        _modeToggleBtn = CreateWindowButton("⇄", ToggleMode, new Color(0.4f, 0.6f, 1f));

        titleRow.AddChild(_minimizeBtn);
        titleRow.AddChild(_maximizeBtn);
        titleRow.AddChild(_modeToggleBtn);
        titleRow.AddChild(_closeBtn);

        _titleBar.GuiInput += OnTitleBarInput;
    }

    private void CreateMainContentArea()
    {
        _contentMargin = new MarginContainer();
        _contentMargin.AddThemeConstantOverride("margin_left", 16);
        _contentMargin.AddThemeConstantOverride("margin_top", 12);
        _contentMargin.AddThemeConstantOverride("margin_right", 16);
        _contentMargin.AddThemeConstantOverride("margin_bottom", 12);
        _contentMargin.SizeFlagsVertical = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
        _windowRoot.AddChild(_contentMargin);

        _content = new VBoxContainer();
        _content.AddThemeConstantOverride("separation", 10);
        _contentMargin.AddChild(_content);

        // 摘要标签
        _summaryLabel = new Label();
        _summaryLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _summaryLabel.AddThemeFontSizeOverride("font_size", ScaledFontSize(13));
        _content.AddChild(_summaryLabel);

        // 操作按钮行
        var buttonRow = new HBoxContainer();
        buttonRow.Alignment = BoxContainer.AlignmentMode.Center;
        buttonRow.AddThemeConstantOverride("separation", 10);
        _content.AddChild(buttonRow);

        _refreshBtn = new Button();
        _refreshBtn.Text = L.T("Refresh");
        _refreshBtn.CustomMinimumSize = new Vector2(100, 36);
        _refreshBtn.Pressed += OnRefreshClicked;
        buttonRow.AddChild(_refreshBtn);

        _exportBtn = new Button();
        _exportBtn.Text = L.T("Export");
        _exportBtn.CustomMinimumSize = new Vector2(100, 36);
        _exportBtn.Pressed += OnExportClicked;
        buttonRow.AddChild(_exportBtn);

        _importBtn = new Button();
        _importBtn.Text = L.T("Import");
        _importBtn.CustomMinimumSize = new Vector2(100, 36);
        _importBtn.Pressed += OnImportClicked;
        buttonRow.AddChild(_importBtn);

        _applyOrderBtn = new Button();
        _applyOrderBtn.Text = L.T("ApplyOrder");
        _applyOrderBtn.CustomMinimumSize = new Vector2(100, 36);
        _applyOrderBtn.Pressed += OnApplyOrderClicked;
        _applyOrderBtn.Visible = false;
        buttonRow.AddChild(_applyOrderBtn);

        _copyDiffBtn = new Button();
        _copyDiffBtn.Text = L.T("CopyDiff");
        _copyDiffBtn.CustomMinimumSize = new Vector2(100, 36);
        _copyDiffBtn.Pressed += OnCopyDiffClicked;
        buttonRow.AddChild(_copyDiffBtn);

        // 配置文件选择器
        var profileRow = new HBoxContainer();
        profileRow.AddThemeConstantOverride("separation", 8);
        _content.AddChild(profileRow);

        _profileLabel = new Label();
        _profileLabel.Text = L.T("ProfileLabel");
        _profileLabel.AddThemeFontSizeOverride("font_size", ScaledFontSize(12));
        profileRow.AddChild(_profileLabel);

        _profileSelector = new OptionButton();
        _profileSelector.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
        _profileSelector.ItemSelected += OnProfileSelected;
        profileRow.AddChild(_profileSelector);

        _deleteProfileBtn = new Button();
        _deleteProfileBtn.Text = L.T("DeleteIcon");
        _deleteProfileBtn.TooltipText = L.T("DeleteProfileTooltip");
        _deleteProfileBtn.CustomMinimumSize = new Vector2(32, 32);
        _deleteProfileBtn.Pressed += OnDeleteProfileClicked;
        _deleteProfileBtn.Visible = false;
        profileRow.AddChild(_deleteProfileBtn);

        _searchLineEdit = new LineEdit();
        _searchLineEdit.PlaceholderText = L.T("SearchPlaceholder");
        _searchLineEdit.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
        _searchLineEdit.TextChanged += OnSearchTextChanged;
        profileRow.AddChild(_searchLineEdit);

        // 差异列表区域（可滚动）
        var scrollContainer = new ScrollContainer();
        scrollContainer.SizeFlagsVertical = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
        _content.AddChild(scrollContainer);

        _diffList = new VBoxContainer();
        _diffList.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
        _diffList.AddThemeConstantOverride("separation", 6);
        scrollContainer.AddChild(_diffList);

        // 底部提示（RichTextLabel 支持 FitContent，自动根据换行内容调整高度，避免文字被截断）
        _hintLabel = new RichTextLabel();
        _hintLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _hintLabel.AddThemeFontSizeOverride("normal_font_size", ScaledFontSize(11));
        _hintLabel.Modulate = new Color(0.5f, 0.5f, 0.5f);
        _hintLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _hintLabel.FitContent = true;
        _hintLabel.BbcodeEnabled = true;
        _hintLabel.ScrollActive = false;
        _hintLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        _hintLabel.CustomMinimumSize = new Vector2(0, 24);
        _hintLabel.Visible = true;
    }

    private void CreateCodeLayer()
    {
        _codeLayer = new VBoxContainer();
        _codeLayer.AddThemeConstantOverride("separation", 10);
        _codeLayer.Visible = false;
        _content.AddChild(_codeLayer);

        _codeHintLabel = new Label();
        _codeHintLabel.Text = L.T("CodeCompareHint");
        _codeHintLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _codeHintLabel.AddThemeFontSizeOverride("font_size", ScaledFontSize(12));
        _codeHintLabel.Modulate = new Color(0.6f, 0.6f, 0.7f);
        _codeLayer.AddChild(_codeHintLabel);

        _codeInput = new PasteDetectTextEdit();
        _codeInput.CustomMinimumSize = new Vector2(0, 80);
        _codeInput.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
        _codeInput.PlaceholderText = L.T("PasteCodeHere");
        _codeInput.WrapMode = TextEdit.LineWrappingMode.Boundary;
        _codeInput.Pasted += OnCodePasted;
        _codeLayer.AddChild(_codeInput);

        // v2.10.5: 初始化编码历史管理器（从 code_history.json 恢复）
        _codeHistory = new CodeHistoryManager();

        _codeHistoryDropdown = new OptionButton();
        _codeHistoryDropdown.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
        _codeHistoryDropdown.ItemSelected += OnCodeHistorySelected;
        _codeHistoryDropdown.AddItem(L.T("CodeHistoryPlaceholder"), 0);
        _codeHistoryDropdown.Select(0);
        _codeLayer.AddChild(_codeHistoryDropdown);

        var codeBtnRow = new HBoxContainer();
        codeBtnRow.Alignment = BoxContainer.AlignmentMode.Center;
        codeBtnRow.AddThemeConstantOverride("separation", 10);
        _codeLayer.AddChild(codeBtnRow);

        _genCodeBtn = new Button();
        _genCodeBtn.Text = L.T("GenerateCode");
        _genCodeBtn.CustomMinimumSize = new Vector2(110, 36);
        _genCodeBtn.Pressed += OnGenerateCode;
        codeBtnRow.AddChild(_genCodeBtn);

        _compareCodeBtn = new Button();
        _compareCodeBtn.Text = L.T("CompareCode");
        _compareCodeBtn.CustomMinimumSize = new Vector2(110, 36);
        _compareCodeBtn.Pressed += OnCompareCode;
        codeBtnRow.AddChild(_compareCodeBtn);

        _codeResultLabel = new Label();
        _codeResultLabel.HorizontalAlignment = HorizontalAlignment.Left;
        _codeResultLabel.AddThemeFontSizeOverride("font_size", 12);
        _codeResultLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _codeLayer.AddChild(_codeResultLabel);

        _applyEncodingOrderBtn = new Button();
        _applyEncodingOrderBtn.Text = L.T("ApplyEncodingOrder");
        _applyEncodingOrderBtn.CustomMinimumSize = new Vector2(130, 36);
        _applyEncodingOrderBtn.Pressed += OnApplyEncodingOrderClicked;
        _applyEncodingOrderBtn.Visible = false;
        codeBtnRow.AddChild(_applyEncodingOrderBtn);
    }

    private void CreateResizeHandle()
    {
        _resizeHandle = new ResizeHandleControl();
        float size = PlatformHelper.IsTouchDevice ? PlatformHelper.TouchTargetSize : 20f;
        _resizeHandle.CustomMinimumSize = new Vector2(size, size);
        _resizeHandle.Size = new Vector2(size, size);
        _resizeHandle.MouseFilter = MouseFilterEnum.Stop;
        AddChild(_resizeHandle);
        _resizeHandle.GuiInput += OnResizeHandleInput;
    }

    /// <summary>
    /// 从持久化状态恢复窗口位置和大小，返回是否有已保存的状态
    /// </summary>
	private bool ApplyWindowState()
	{
		var state = _windowState.Load();
		if (state == null) return false;

		// 校验旧版本可能产生的脏数据：非最大化但尺寸接近全屏
		// （v1.2.3-dev2 之前 _currentSize 滞后一帧，还原时 save 会写入全屏尺寸但 IsMaximized=false）
		var viewport = GetViewportRect().Size;
		if (!state.IsMaximized && (state.Width >= viewport.X * 0.95f || state.Height >= viewport.Y * 0.95f))
		{
			GD.Print($"[ModSyncChecker] Discarding stale window state (size {state.Width}x{state.Height} near viewport {viewport.X}x{viewport.Y}, IsMaximized=false)");
			return false;
		}
		// 校验位置是否完全超出屏幕
		if (state.PositionX + state.Width < -50 || state.PositionY + state.Height < -50
			|| state.PositionX > viewport.X + 50 || state.PositionY > viewport.Y + 50)
		{
			GD.Print($"[ModSyncChecker] Discarding off-screen window state (pos {state.PositionX},{state.PositionY})");
			return false;
		}

		// 应用位置
		AnchorLeft = AnchorTop = AnchorRight = AnchorBottom = 0;
		OffsetLeft = state.PositionX;
		OffsetTop = state.PositionY;
		OffsetRight = state.PositionX + state.Width;
		OffsetBottom = state.PositionY + state.Height;

		_normalPosition = new Vector2(state.PositionX, state.PositionY);
		_normalSize = new Vector2(
			Mathf.Max(state.Width, MinWindowSize.X),
			Mathf.Max(state.Height, MinWindowSize.Y)
		);

		// 如果加载的尺寸低于最小限制，更新 offset 使其 clamp 到 MinWindowSize
		if (state.Width < MinWindowSize.X || state.Height < MinWindowSize.Y)
		{
			OffsetRight = OffsetLeft + _normalSize.X;
			OffsetBottom = OffsetTop + _normalSize.Y;
		}

	// 应用最大化状态
	if (state.IsMaximized)
	{
		_isMaximized = true;
		_maximizeBtn.Text = "❐";
		OffsetLeft = 0;
		OffsetTop = 0;
		OffsetRight = viewport.X;
		OffsetBottom = viewport.Y;
	}

	// v2.0.0: 恢复面板模式
	_isCodeMode = state.IsCodeMode;

	return true;
	}

    /// <summary>
    /// 保存当前窗口状态到配置文件
    /// </summary>
    private void SaveWindowState()
    {
        if (_windowState == null) return;

		Vector2 pos, size;
		if (_isMaximized)
		{
			pos = _normalPosition;
			size = _normalSize;
		}
		else if (_isMinimized)
		{
			// 最小化时保存正常位置和尺寸，而非当前的折叠尺寸（会随拖拽更新 _normalPosition）
			// 否则下次加载时 _normalSize=42px，面板永远只有标题栏高度
			pos = _normalPosition;
			size = _normalSize;
		}
		else
		{
			pos = new Vector2(OffsetLeft, OffsetTop);
			// 用 offset 差值而非 _currentSize，因为 _currentSize 由 _Process 更新可能滞后一帧
			size = new Vector2(OffsetRight - OffsetLeft, OffsetBottom - OffsetTop);
		}

        // v2.1.2: 保存前校验 LastProfilePath 对应文件确实存在，避免持久化脏路径
        string? validProfilePath = null;
        if (_selectedProfilePath != null && File.Exists(_selectedProfilePath))
            validProfilePath = _selectedProfilePath;

        _windowState.Save(new WindowStateData
        {
            PositionX = pos.X,
            PositionY = pos.Y,
            Width = size.X,
            Height = size.Y,
            IsMaximized = _isMaximized,
            IsCodeMode = _isCodeMode,
            LastProfilePath = validProfilePath // v2.1.2: 精确路径，替代 Contains 子串匹配
        });
    }

    /// <summary>
    /// 语言切换后重新构建所有静态 UI 文本
    /// </summary>
    private void RebuildStaticUI()
    {
        // Import/export panel
        _titleLabel.Text = L.T("Title");
        _refreshBtn.Text = L.T("Refresh");
        _exportBtn.Text = L.T("Export");
        _importBtn.Text = L.T("Import");
        _applyOrderBtn.Text = L.T("ApplyOrder");
        _profileLabel.Text = L.T("ProfileLabel");
        _searchLineEdit.PlaceholderText = L.T("SearchPlaceholder");
        _deleteProfileBtn.TooltipText = L.T("DeleteProfileTooltip");

        // Mode toggle
        _modeToggleBtn.Text = _isCodeMode ? L.T("TabEncoding") : L.T("TabImport");

        // Encoding panel (v2.10.7: follow game language)
        if (_genCodeBtn != null)
            _genCodeBtn.Text = L.T("GenerateCode");
        if (_compareCodeBtn != null)
            _compareCodeBtn.Text = L.T("CompareCode");
        if (_applyEncodingOrderBtn != null)
            _applyEncodingOrderBtn.Text = L.T("ApplyEncodingOrder");
        if (_codeInput != null)
            _codeInput.PlaceholderText = L.T("PasteCodeHere");
        // Tag element stored in codeHint local var — need field
        if (_codeHistoryDropdown != null && _codeHistoryDropdown.ItemCount > 0)
            _codeHistoryDropdown.SetItemText(0, L.T("CodeHistoryPlaceholder"));

        // Encoding panel hint label
        if (_codeHintLabel != null)
            _codeHintLabel.Text = L.T("CodeCompareHint");
        if (_copyDiffBtn != null)
            _copyDiffBtn.Text = L.T("CopyDiff");

        // 刷新下拉框第一项（默认提示）
        if (_profileSelector.ItemCount > 0)
        {
            _profileSelector.SetItemText(0, L.T("SelectProfile"));
        }

        // 重新刷新当前显示内容以更新所有动态文本
        if (_currentProfile != null)
        {
            DisplayProfileComparison(_currentProfile);
        }
        else
        {
            OnRefreshClicked();
        }
    }

    /// <summary>
    /// 创建标题栏窗口控制按钮
    /// </summary>
    private static Button CreateWindowButton(string text, Action onPressed, Color accentColor)
    {
        var btn = new Button();
        btn.Text = text;
        btn.CustomMinimumSize = new Vector2(28, 28);
        btn.Size = new Vector2(28, 28);
        btn.Flat = true;
        btn.AddThemeFontSizeOverride("font_size", 14);
        btn.Pressed += onPressed;

        var hoverStyle = new StyleBoxFlat
        {
            BgColor = new Color(accentColor.R, accentColor.G, accentColor.B, 0.3f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4
        };
        btn.AddThemeStyleboxOverride("hover", hoverStyle);

        return btn;
    }

    // ===== 标题栏拖拽 =====
    private void OnTitleBarInput(InputEvent @event)
    {
        // 按钮点击帧不启动拖拽，避免与 OnMaximizeClicked/OnMinimizeClicked 冲突
        if (_buttonClickFrame)
        {
            _buttonClickFrame = false;
            return;
        }

        if (@event is InputEventMouseButton mouseBtn && mouseBtn.ButtonIndex == MouseButton.Left)
        {
            if (mouseBtn.Pressed)
            {
                _isDragging = true;
                // 转为 anchor=0 的 offset 模式以便自由拖动
                var currentPos = new Vector2(OffsetLeft, OffsetTop);
                if (AnchorLeft != 0 || AnchorTop != 0)
                {
                    currentPos = Position;
                    AnchorLeft = AnchorTop = AnchorRight = AnchorBottom = 0;
                }
				float panelW = OffsetRight - OffsetLeft;
				float panelH = OffsetBottom - OffsetTop;
				OffsetLeft = currentPos.X;
				OffsetTop = currentPos.Y;
				OffsetRight = currentPos.X + panelW;
				OffsetBottom = currentPos.Y + panelH;
                _dragStartMouse = GetGlobalMousePosition();
                _dragStartOffset = currentPos;
            }
            else
            {
                _isDragging = false;
                SaveWindowState(); // 拖拽结束后保存位置
            }
            AcceptEvent();
        }
    }

    // ===== 缩放手柄事件 =====
    private void OnResizeHandleInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseBtn && mouseBtn.ButtonIndex == MouseButton.Left)
        {
            if (mouseBtn.Pressed)
            {
                _isResizing = true;
				_resizeStartMouse = GetGlobalMousePosition();
				// 用 offset 差值而非 _currentSize（输入回调在 _Process 之前运行，_currentSize 滞后一帧）
				_resizeStartSize = new Vector2(OffsetRight - OffsetLeft, OffsetBottom - OffsetTop);
                // 同样需要 anchor=0 模式
                if (AnchorLeft != 0)
                {
                    var currentPos = Position;
                    AnchorLeft = AnchorTop = AnchorRight = AnchorBottom = 0;
					float rPanelW = OffsetRight - OffsetLeft;
					float rPanelH = OffsetBottom - OffsetTop;
					OffsetLeft = currentPos.X;
					OffsetTop = currentPos.Y;
					OffsetRight = currentPos.X + rPanelW;
					OffsetBottom = currentPos.Y + rPanelH;
                }
                // 如果正在最大化，先还原
                if (_isMaximized)
                {
                    OnMaximizeClicked();
                }
            }
            else
            {
                _isResizing = false;
                SaveWindowState(); // 缩放结束后保存大小
            }
            AcceptEvent();
        }
    }

    // ===== 窗口控制 =====
    private void OnMinimizeClicked()
    {
        _isDragging = false;
        _buttonClickFrame = true;

		if (_isMinimized)
		{
			// 还原：直接恢复窗口大小，VBoxContainer 自动重排内容
			// 先解除裁剪，再恢复大小，避免还原后出现黑框
			if (_mainPanel != null) _mainPanel.ClipContents = false;
			_isMinimized = false;
			OffsetRight = OffsetLeft + _normalSize.X;
			OffsetBottom = OffsetTop + _normalSize.Y;
			_currentSize = _normalSize;
			_minimizeBtn.Text = "—";
		}
		else
		{
			// 最小化：只保留标题栏高度。启用 ClipContents 裁剪溢出内容，
			// 避免子节点因 CustomMinimumSize 过大而溢出面板边界（导致"透明"/"不收敛"）
			if (_mainPanel != null) _mainPanel.ClipContents = true;
			bool wasMaximized = _isMaximized;
			_isMinimized = true;
			_isMaximized = false;
			_maximizeBtn.Text = "□";
			// 仅当之前不是最大化状态时才保存 _normalSize（最大化时 _normalSize 已是正常窗口大小）
			if (!wasMaximized)
				// 用 offset 差值而非 _currentSize（输入回调在 _Process 之前运行，_currentSize 滞后一帧）
				_normalSize = new Vector2(OffsetRight - OffsetLeft, OffsetBottom - OffsetTop);
			float minHeight = TitleBarHeight + 6f;
			OffsetRight = OffsetLeft + _normalSize.X;
			OffsetBottom = OffsetTop + minHeight;
			_minimizeBtn.Text = "▢";
		}
        SaveWindowState();
    }

    private void OnMaximizeClicked()
    {
        _isDragging = false;
        _buttonClickFrame = true;

        if (_isMaximized)
        {
            // 还原到正常状态
            _isMaximized = false;
            _maximizeBtn.Text = "□";

            AnchorLeft = AnchorTop = AnchorRight = AnchorBottom = 0;
            OffsetLeft = _normalPosition.X;
            OffsetTop = _normalPosition.Y;
            OffsetRight = _normalPosition.X + _normalSize.X;
            OffsetBottom = _normalPosition.Y + _normalSize.Y;
        }
        else
        {
            // 保存当前状态
			_normalPosition = new Vector2(OffsetLeft, OffsetTop);
			// 用 offset 差值而非 _currentSize（输入回调在 _Process 之前运行，_currentSize 滞后一帧）
			_normalSize = new Vector2(OffsetRight - OffsetLeft, OffsetBottom - OffsetTop);
			_isMaximized = true;
            _maximizeBtn.Text = "❐";
            _isMinimized = false;
            _minimizeBtn.Text = "—";

            // 扩展到全屏
            var viewport = GetViewportRect().Size;
            AnchorLeft = AnchorTop = AnchorRight = AnchorBottom = 0;
            OffsetLeft = 0;
            OffsetTop = 0;
            OffsetRight = viewport.X;
            OffsetBottom = viewport.Y;
        }
        SaveWindowState();
    }

    // ===== 每帧处理：拖拽、缩放、手柄位置 =====
    public override void _Process(double delta)
    {
        // 每帧重置按钮点击标记（仅用于当前输入帧内阻断 OnTitleBarInput）
        _buttonClickFrame = false;

        // 安全释放：鼠标在窗口外松开时也能结束拖动/缩放
        if (_isDragging && !Input.IsMouseButtonPressed(MouseButton.Left))
        {
            _isDragging = false;
            SaveWindowState();
        }
        if (_isResizing && !Input.IsMouseButtonPressed(MouseButton.Left))
        {
            _isResizing = false;
            SaveWindowState();
        }

        // 拖拽移动
        if (_isDragging)
        {
            var mouseDelta = GetGlobalMousePosition() - _dragStartMouse;
            var newPos = _dragStartOffset + mouseDelta;

            // 最小化时用当前实际尺寸做边界限制，而非 _normalSize（折叠高度只有 ~42px）
            var viewport = GetViewportRect().Size;
            float clampW = _isMinimized ? (OffsetRight - OffsetLeft) : _normalSize.X;
            float clampH = _isMinimized ? (OffsetBottom - OffsetTop) : _normalSize.Y;
            newPos.X = Mathf.Clamp(newPos.X, 0, Mathf.Max(0, viewport.X - clampW));
            newPos.Y = Mathf.Clamp(newPos.Y, 0, Mathf.Max(0, viewport.Y - clampH));

			OffsetLeft = newPos.X;
			OffsetTop = newPos.Y;
			// 最小化时保持当前高度，不要用 _normalSize 展开
			OffsetRight = newPos.X + clampW;
			OffsetBottom = newPos.Y + clampH;

            _normalPosition = newPos;
        }

        // 缩放
        if (_isResizing)
        {
            var mouseDelta = GetGlobalMousePosition() - _resizeStartMouse;
            var newSize = new Vector2(
                Mathf.Max(_resizeStartSize.X + mouseDelta.X, MinWindowSize.X),
                Mathf.Max(_resizeStartSize.Y + mouseDelta.Y, MinWindowSize.Y)
            );

            // 限制最大尺寸不超过视口
            var viewport = GetViewportRect().Size;
            newSize.X = Mathf.Min(newSize.X, viewport.X - OffsetLeft);
            newSize.Y = Mathf.Min(newSize.Y, viewport.Y - OffsetTop);

            OffsetRight = OffsetLeft + newSize.X;
            OffsetBottom = OffsetTop + newSize.Y;
            _normalSize = newSize;
        }

        // 计算当前实际宽高（Anchor/Offset 模式下 Size 属性可能滞后一帧，直接用 offset 差值更可靠）
        float currentW = OffsetRight - OffsetLeft;
        float currentH = OffsetBottom - OffsetTop;
        _currentSize = new Vector2(currentW, currentH);

        // 同步主面板和内部容器大小（手动控制，彻底排除 Container 布局干扰）
        if (_mainPanel != null)
        {
            _mainPanel.Size = _currentSize;
            _mainPanel.Position = Vector2.Zero;
            // 最小化时裁剪溢出内容（子节点 CustomMinimumSize 可能超出面板范围），还原时取消裁剪
            _mainPanel.ClipContents = _isMinimized;
        }
        if (_windowRoot != null)
        {
            _windowRoot.Size = _currentSize;
            _windowRoot.Position = Vector2.Zero;
        }

        // 更新缩放手柄位置（相对根节点右下角）
        if (_resizeHandle != null)
        {
            float handleSize = PlatformHelper.IsTouchDevice ? PlatformHelper.TouchTargetSize : 20f;
            _resizeHandle.Position = new Vector2(currentW - handleSize, currentH - handleSize);
            // 最小化时隐藏手柄
            _resizeHandle.Visible = !_isMinimized;
        }

        // 底部提示文字手动定位（改为根节点子节点，避免 VBoxContainer 空间竞争挤出边界）
        if (_hintLabel != null && !_isMinimized)
        {
            // 先设置宽度让 FitContent 计算正确高度
            float hintMaxW = currentW - 24f; // 左右各留 12px
            if (hintMaxW > 100f)
            {
                _hintLabel.Size = new Vector2(hintMaxW, 0);
                // FitContent 后高度可能有一帧延迟，用保底值
                float hintH = Mathf.Max(_hintLabel.Size.Y, _hintLabel.CustomMinimumSize.Y);
                float hintY = currentH - hintH - 6f; // 底部留 6px
                _hintLabel.Position = new Vector2(12f, hintY);
                _hintLabel.Visible = currentH > 60f;
            }
            else
            {
                _hintLabel.Visible = false;
            }
        }
        else if (_hintLabel != null)
        {
            _hintLabel.Visible = false;
        }

        // v2.4.2: Skip i18n polling on Android
        if (!OS.HasFeature("mobile"))
        {
            _langCheckFrameCounter++;
            if (_langCheckFrameCounter % 300 == 0)
            {
                string oldLang = L.CurrentLanguage;
                L.Reload();
                if (L.CurrentLanguage != oldLang)
                {
                    _lastLocale = L.CurrentLanguage;
                    RebuildStaticUI();
                }
            }
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            if (keyEvent.Keycode == Key.K)
            {
                GD.Print($"[ModSyncChecker] K pressed, toggling panel. Current visible: {_isVisible}");
                if (_isVisible)
                {
                    SaveWindowState(); // v2.0.0: 关闭前保存当前模式
                    HidePanel();
                }
                else
                    ShowPanel();
                GetViewport().SetInputAsHandled();
            }
            else if (keyEvent.Keycode == Key.Escape && _isVisible)
            {
                SaveWindowState(); // v2.0.0: 关闭前保存当前模式
                HidePanel();
                GetViewport().SetInputAsHandled();
            }
        }
    }

    public void ShowPanel(List<ModDifference>? differences = null)
    {
        _isVisible = true;
        Visible = true;
        if (GodotObject.IsInstanceValid(_mainPanel))
            _mainPanel.Show();
        if (GodotObject.IsInstanceValid(_applyOrderBtn))
            _applyOrderBtn.Visible = false;

        // v2.1.2: 每次打开面板时刷新配置列表，确保 _currentProfiles 与磁盘同步
        RefreshProfileList();

        if (differences != null)
        {
            // v2.1.2: 连接失败差异视图 — 清当前profile显示diff，但保留_selectedProfilePath
            // 下次手动打开时仍能恢复上次选择的配置文件
            _currentProfile = null;
            _deleteProfileBtn.Visible = false;
            var localMods = ModSyncCore.ScanLocalMods();
            DisplayDifferences(differences, localMods);
        }
        else if (_selectedProfilePath != null)
        {
            // v2.1.2: 恢复上次选择的配置文件视图（核心修复）
            // 精确路径匹配找到下拉框索引
            bool found = false;
            for (int i = 0; i < _currentProfiles.Count; i++)
            {
                if (_currentProfiles[i] == _selectedProfilePath)
                {
                    // v2.1.2: Select() 本身就会触发 ItemSelected → OnProfileSelected，
                    // 移除冗余显式调用避免重复执行 DisplayProfileComparison
                    _profileSelector.Select(i + 1); // index 0 = "SelectProfile" placeholder
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                // 配置文件已被删除或移动 — 清理并退回到原始列表
                _selectedProfilePath = null;
                // v2.1.2: 重置下拉框到占位项，触发 OnProfileSelected(0) 清理状态
                _profileSelector.Select(0);
                OnRefreshClicked();
            }
        }
        else
        {
            OnRefreshClicked();
        }
    }

    public void ShowPanel(List<string> remoteMods, List<string>? missingOnHost = null, List<string>? missingOnLocal = null)
    {
        var localMods = ModSyncCore.ScanLocalMods();
        var differences = ModSyncCore.CompareMods(localMods, remoteMods, missingOnHost, missingOnLocal);
        ShowPanelWithLocalMods(differences, localMods);
    }

    private void ShowPanelWithLocalMods(List<ModDifference> differences, List<ModInfoSnapshot> localMods)
    {
        _isVisible = true;
        Visible = true;
        if (GodotObject.IsInstanceValid(_mainPanel))
            _mainPanel.Show();
        if (GodotObject.IsInstanceValid(_applyOrderBtn))
            _applyOrderBtn.Visible = false;
        _currentProfile = null;
        _deleteProfileBtn.Visible = false;
        // v2.1.2: 连接差异视图不覆盖已选配置文件（保留 _selectedProfilePath），
        // 但下拉框视觉效果应重置，避免显示已不相关的旧选项
        if (_profileSelector != null)
            _profileSelector.Select(0);
        DisplayDifferences(differences, localMods);
    }

    // ===== v2.1.0: 图层切换 =====

    /// <summary>
    /// 根据 _isCodeMode 切换显示编码图层或导入导出图层
    /// </summary>
    private void ApplyLayerMode()
    {
        if (_content == null || _codeLayer == null) return;

        _modeToggleBtn.Text = _isCodeMode ? L.T("TabEncoding") : L.T("TabImport");

        // 隐藏/显示导入导出控件 (_content 的前4个子节点: summary, buttonRow, profileRow, scrollContainer)
        for (int i = 0; i < _content.GetChildCount(); i++)
        {
            var child = _content.GetChild(i);
            if (child == _codeLayer) continue;
            if (child is Control ctrl) ctrl.Visible = !_isCodeMode;
        }

        // 显示/隐藏编码图层
        _codeLayer.Visible = _isCodeMode;
    }

    private void ToggleMode()
    {
        _isCodeMode = !_isCodeMode;
        ApplyLayerMode();
    }

    private void OnGenerateCode()
    {
        var localMods = ModSyncCore.ScanLocalMods();
        var mediumCode = GenerateSyncCode(localMods);
        _codeInput.Text = mediumCode;

        // v2.10.0: 保存到编码历史缓存（仅中码）
        AddToCodeHistory(mediumCode);

        // v2.10.0: 复制中码到剪贴板（gzip+Base64，自包含完整数据）
        DisplayServer.ClipboardSet(mediumCode);
        _codeResultLabel.Text = L.T("CodeGenerated") + "\n" + mediumCode;

        // v2.10.0: 生成代码后显示排序按钮（用户可能想把编码发给别人排序）
        _applyEncodingOrderBtn.Visible = true;
    }

    private void OnCompareCode()
    {
        var input = _codeInput.Text.StripEdges();
        if (string.IsNullOrEmpty(input))
        {
            _codeResultLabel.Text = L.T("CodeEmpty");
            return;
        }
        var result = DecodeAndCompare(input);
        _codeResultLabel.Text = result;
        if (input.StartsWith("#MSCv2#"))
            _applyEncodingOrderBtn.Visible = true;
    }

    /// <summary>
    /// v2.10.0: 粘贴自动检测 — 剪贴板内容匹配 #MSCv2# 格式时自动触发对比 + 显示排序按钮
    /// </summary>
    private void OnCodePasted()
    {
        var text = _codeInput.Text.StripEdges();
        if (text.StartsWith("#MSCv2#"))
        {
            var result = DecodeAndCompare(text);
            _codeResultLabel.Text = result;
            _applyEncodingOrderBtn.Visible = true;
        }
    }

    /// <summary>
    /// v2.3.0: Compute CRC32 hash of a string. Returns 8-char uppercase hex.
    /// </summary>
    private static uint ComputeCrc32(string data)
    {
        uint crc = 0xFFFFFFFF;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(data);
        for (int i = 0; i < bytes.Length; i++)
        {
            crc ^= bytes[i];
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0)
                    crc = (crc >> 1) ^ 0xEDB88320;
                else
                    crc >>= 1;
            }
        }
        return ~crc;
    }

    /// <summary>
    /// v2.7.0: Gzip compress a string and return Base64.
    /// </summary>
    private static string GzipCompress(string data)
    {
        byte[] rawBytes = System.Text.Encoding.UTF8.GetBytes(data);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(rawBytes, 0, rawBytes.Length);
        }
        return Convert.ToBase64String(output.ToArray());
    }

    /// <summary>
    /// v2.7.0: Decompress a gzip+Base64 string. Returns null on failure.
    /// </summary>
    private static string? GzipDecompress(string base64)
    {
        try
        {
            byte[] compressed = Convert.FromBase64String(base64);
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return System.Text.Encoding.UTF8.GetString(output.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// v2.10.0: 仅中码(gzip+Base64) — 长码已删除，每项含OrderIndex可恢复排序
    /// 中码: #MSCv2#base64#CRC32（自包含可离线对比）
    /// </summary>
    private string GenerateSyncCode(List<ModInfoSnapshot> mods)
    {
        // Sort by load order for consistent encoding
        var sorted = mods.OrderBy(m => m.LoadOrder).ToList();

        // Compact format — v2|count|id~ver~source~orderIdx|...  (orderIdx restores sort)
        var parts = new List<string> { $"v2|{sorted.Count}" };
        for (int i = 0; i < sorted.Count; i++)
        {
            var m = sorted[i];
            parts.Add($"{m.Id}~{m.Version}~{m.ModSource}~{i}");
        }

        // Compute CRC32 on the data portion (before appending CRC)
        string dataWithoutCrc = string.Join("|", parts);
        uint crc = ComputeCrc32(dataWithoutCrc);
        string crcHex = crc.ToString("X8"); // 8 uppercase hex chars

        // v2.10.0: 仅中码(gzip+Base64)，不再生成长码
        string compressed = GzipCompress(dataWithoutCrc);
        string mediumCode = $"#MSCv2#{compressed}#{crcHex}";        // ~80-120 chars, self-contained

        return mediumCode;
    }

    /// <summary>
    /// v2.10.0: 仅中码对比 — #MSCv2#base64#CRC32（自包含完整数据）
    /// 旧短码格式 #MSCv2#CRC32（仍兼容hash对比，不再生成）
    /// 不再支持长码多行输入（v2.10.0删除长码）
    /// </summary>
    private string DecodeAndCompare(string code)
    {
        try
        {
            code = code.Trim();
            if (string.IsNullOrEmpty(code)) return L.T("CodeEmpty");

            // Detect short code header
            bool hasShortCode = code.StartsWith("#MSCv2#");
            if (!hasShortCode) return L.T("CodeParseError");

            var afterHeader = code["#MSCv2#".Length..];
            var hashParts = afterHeader.Split('#');
            string? shortPayload = null; // gzip+Base64 compressed data
            string? shortHash = null;     // CRC32 hash (last segment)

            if (hashParts.Length == 1)
            {
                // Old format: #MSCv2#CRC32
                shortHash = hashParts[0];
            }
            else if (hashParts.Length >= 2)
            {
                // v2.7.0+ medium code: #MSCv2#base64#CRC32
                shortPayload = hashParts[0];
                shortHash = hashParts[hashParts.Length - 1]; // last = CRC32
            }

            // Get detail data: from gzip payload or fall back to hash-only comparison
            string? detailLine = null;
            if (shortPayload != null)
            {
                var decompressed = GzipDecompress(shortPayload);
                if (decompressed != null && shortHash != null)
                    detailLine = $"{decompressed}|{shortHash}"; // append CRC for verification
                else if (decompressed != null)
                    detailLine = decompressed;
            }

            // No detail line: hash-only comparison
            if (detailLine == null)
            {
                if (shortHash != null)
                {
                    var modsForHash = ModSyncCore.ScanLocalMods();
                    var localMedium = GenerateSyncCode(modsForHash);
                    if (localMedium.StartsWith("#MSCv2#"))
                    {
                        string localAfter = localMedium["#MSCv2#".Length..];
                        var localParts = localAfter.Split('#');
                        string localHash = localParts[^1];

                        bool match = shortHash.Equals(localHash, StringComparison.OrdinalIgnoreCase);
                        return match
                            ? L.T("CodeSynced") + " (CRC32)"
                            : L.T("CodeHashMismatch") + " - " + L.T("CodeNotSyncedExpand");
                    }
                }
                return L.T("CodeParseError");
            }

            // ----- Parse detail line -----
            // Format: v2|count|id~ver~source~orderIdx|...|CRC32
            var allParts = detailLine.Split('|');
            if (allParts.Length < 2) return L.T("CodeParseError");

            string header = allParts[0];
            if (header != "v2" && header != "MSCv2")
                return L.T("CodeParseError");

            // In new format, last segment is CRC32; separate it
            string? lineCrc = null;
            string[] entryParts;
            if (header == "v2" && allParts.Length >= 3)
            {
                lineCrc = allParts[^1];
                entryParts = allParts[..^1];
            }
            else
            {
                entryParts = allParts;
            }

            // Verify CRC if present
            string hashStatusLine = "";
            if (lineCrc != null)
            {
                // Recompute CRC from data portion (rejoin entryParts)
                string dataToVerify = string.Join("|", entryParts);
                uint recomputed = ComputeCrc32(dataToVerify);
                string recomputedHex = recomputed.ToString("X8");
                if (lineCrc.Equals(recomputedHex, StringComparison.OrdinalIgnoreCase))
                    hashStatusLine = L.T("CodeHashOk") + " (CRC32)";
                else
                    hashStatusLine = L.T("CodeHashMismatch") + $" (data:{recomputedHex})";
            }
            else if (shortHash != null)
            {
                // Old format fallback: verify short hash against whole detail line (MD5 legacy)
                using var md5 = System.Security.Cryptography.MD5.Create();
                byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(detailLine));
                string computedHash = BitConverter.ToString(hash).Replace("-", "")[..8];
                hashStatusLine = shortHash.Equals(computedHash, StringComparison.OrdinalIgnoreCase)
                    ? L.T("CodeHashOk") + " (MD5 legacy)"
                    : L.T("CodeHashMismatch");
            }

            if (!int.TryParse(entryParts[1], out int remoteCount))
                return L.T("CodeParseError");

            // Parse remote mod entries
            // v2 format (v2):         id~ver~source~orderIdx (4 fields, v2.8.0+)
            // v2 format (v2.7.0):     id~ver~source (3 fields, backward compat)
            // Old format (MSCv2):      id~ver~source~order~enabled (5 fields) or id~ver~order (3 fields legacy)
            var remoteEntries = new List<RemoteModEntry>();
            bool isV2 = header == "v2";
            bool isOld = header == "MSCv2";
            for (int i = 2; i < entryParts.Length; i++)
            {
                var seg = entryParts[i].Split('~');
                if (seg.Length < 2) continue;

                var entry = new RemoteModEntry
                {
                    Id = seg[0],
                    Version = seg[1],
                    Source = seg.Length >= 3 && int.TryParse(seg[2], out int src) ? src : 1,
                    Enabled = true
                };

                if (isV2 && seg.Length >= 4)
                {
                    // v2.8.0: id~ver~source~orderIdx
                    int.TryParse(seg[3], out entry.OrderIndex);
                    entry.Order = entry.OrderIndex;
                }
                else if (isOld && seg.Length >= 5)
                {
                    // Old extended: id~ver~source~order~enabled
                    int.TryParse(seg[3], out entry.Order);
                    entry.Enabled = seg[4] == "1";
                }
                else if (isOld && seg.Length >= 3)
                {
                    // Old legacy: id~ver~order
                    int.TryParse(seg[2], out entry.Order);
                }

                remoteEntries.Add(entry);
            }

            // Scan local mods
            var localMods = ModSyncCore.ScanLocalMods();
            var localDict = localMods.ToDictionary(m => m.Id, m => m);
            var remoteDict = remoteEntries.ToDictionary(e => e.Id, e => e);

            var resultLines = new List<string>();

            // 1. Missing locally (remote has, local doesn't)
            int missingLocal = 0;
            foreach (var re in remoteEntries)
            {
                if (!localDict.ContainsKey(re.Id))
                {
                    resultLines.Add($"{re.Id} — " + L.T("DiffMissingLocal"));
                    missingLocal++;
                }
            }

            // 2. Extra locally (local has, remote doesn't — gameplay-affecting only)
            int extraLocal = 0;
            foreach (var local in localMods.Where(m => m.AffectsGameplay))
            {
                if (!remoteDict.ContainsKey(local.Id))
                {
                    resultLines.Add($"{local.Name} ({local.Id}) — " + L.T("DiffExtra"));
                    extraLocal++;
                }
            }

            // 3. Per-mod comparison: version, source, order
            int verMismatch = 0, srcMismatch = 0, orderMismatch = 0;
            foreach (var local in localMods)
            {
                if (!remoteDict.TryGetValue(local.Id, out var remote)) continue;

                if (!string.Equals(local.Version, remote.Version, StringComparison.OrdinalIgnoreCase))
                {
                    resultLines.Add($"{local.Name} — " + L.TF("VersionDetailFmt", local.Version, remote.Version));
                    verMismatch++;
                }

                if (remote.Source > 0 && local.ModSource != remote.Source)
                {
                    resultLines.Add($"{local.Name} — " + L.T("DiffSource") + $" ({local.ModSource}≠{remote.Source})");
                    srcMismatch++;
                }
            }

            // 3b. v2.8.0: Order comparison using OrderIndex
            if (remoteEntries.Any(e => e.OrderIndex > 0))
            {
                var remoteOrdered = remoteEntries.OrderBy(e => e.OrderIndex).ToList();
                var localOrdered = localMods.OrderBy(m => m.LoadOrder).ToList();
                int maxCheck = Math.Min(remoteOrdered.Count, localOrdered.Count);
                for (int i = 0; i < maxCheck; i++)
                {
                    if (remoteOrdered[i].Id != localOrdered[i].Id)
                    {
                        resultLines.Add($"[#{i}] {localOrdered[i].Name} ≠ {remoteOrdered[i].Id} — " + L.T("DiffOrder"));
                        orderMismatch++;
                    }
                }
            }

            // 4. Extra tool mods (safe, not in remote list)
            int extraTool = 0;
            foreach (var local in localMods.Where(m => !m.AffectsGameplay))
            {
                if (!remoteDict.ContainsKey(local.Id))
                    extraTool++;
            }
            if (extraTool > 0)
                resultLines.Add(L.T("GroupExtraTool") + $" ×{extraTool}");

            // Build summary header
            var summaryParts = new List<string>();
            if (!string.IsNullOrEmpty(hashStatusLine)) summaryParts.Add(hashStatusLine);

            int totalDiffs = missingLocal + extraLocal + verMismatch + srcMismatch + orderMismatch;
            if (totalDiffs == 0)
            {
                summaryParts.Add(L.T("CodeSynced"));
                return string.Join("\n", summaryParts);
            }

            if (missingLocal > 0) summaryParts.Add($"{L.T("DiffMissingLocal")}:{missingLocal}");
            if (extraLocal > 0) summaryParts.Add($"{L.T("DiffExtra")}:{extraLocal}");
            if (verMismatch > 0) summaryParts.Add($"{L.T("DiffVersion")}:{verMismatch}");
            if (srcMismatch > 0) summaryParts.Add($"{L.T("DiffSource")}:{srcMismatch}");
            if (orderMismatch > 0) summaryParts.Add($"{L.T("DiffOrder")}:{orderMismatch}");

            string summary = $"[{remoteCount} mods] " + string.Join(" | ", summaryParts);

            return summary + "\n" + string.Join("\n", resultLines);
        }
        catch (Exception ex)
        {
            return L.T("CodeParseError") + ": " + ex.Message;
        }
    }

    /// <summary>Remote mod entry parsed from sync code (v2.8.0: id+ver+source+orderIdx; backward compat with old formats)</summary>
    private struct RemoteModEntry
    {
        public string Id;
        public string Version;
        public int Source;
        public int Order;
        public int OrderIndex; // v2.8.0: sort position in the encoded mod list (0-based)
        public bool Enabled;
    }

    // ===== v2.2.0: 一键复制差异结果 =====
    private void OnCopyDiffClicked()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ModSync Diff Report");
        sb.AppendLine("===================");

        if (_currentDiffData != null && _currentDiffData.Count > 0)
        {
            // 差异视图模式
            sb.AppendLine(_summaryLabel.Text);
            sb.AppendLine("-------------------");

            var criticalDiffs = _currentDiffData.Where(d => d.Type is DifferenceType.MissingOnLocal or DifferenceType.MissingOnRemote or DifferenceType.VersionMismatch or DifferenceType.StateMismatch or DifferenceType.HashMismatch).ToList();
            var orderDiffs = _currentDiffData.Where(d => d.Type == DifferenceType.OrderMismatch).ToList();
            var extraGameplay = _currentDiffData.Where(d => d.Type == DifferenceType.ExtraMod && !d.IsToolMod).ToList();
            var extraTool = _currentDiffData.Where(d => d.Type == DifferenceType.ExtraMod && d.IsToolMod).ToList();

            if (criticalDiffs.Count > 0)
            {
                sb.AppendLine($"[CRITICAL] ({criticalDiffs.Count}):");
                foreach (var d in criticalDiffs)
                    sb.AppendLine($"  {GetDiffTypeText(d.Type)}: {d.Name} — {d.Details}");
            }
            if (orderDiffs.Count > 0)
            {
                sb.AppendLine($"[ORDER] ({orderDiffs.Count}):");
                foreach (var d in orderDiffs)
                    sb.AppendLine($"  {GetDiffTypeText(d.Type)}: {d.Name} — {d.Details}");
            }
            if (extraGameplay.Count > 0)
            {
                sb.AppendLine($"[EXTRA-GAMEPLAY] ({extraGameplay.Count}):");
                foreach (var d in extraGameplay)
                    sb.AppendLine($"  {d.Name} — {d.Details}");
            }
            if (extraTool.Count > 0)
            {
                sb.AppendLine($"[EXTRA-TOOL] ({extraTool.Count}):");
                foreach (var d in extraTool)
                    sb.AppendLine($"  {d.Name} — {d.Details}");
            }
        }
        else if (_currentLocalModsData != null && _currentLocalModsData.Count > 0)
        {
            // 本地MOD列表视图
            sb.AppendLine(_summaryLabel.Text);
            sb.AppendLine("-------------------");
            int total = _currentLocalModsData.Count;
            int loaded = _currentLocalModsData.Count(m => m.State == ModLoadState.Loaded);
            int failed = _currentLocalModsData.Count(m => m.State == ModLoadState.Failed);
            int disabled = _currentLocalModsData.Count(m => m.State == ModLoadState.Disabled);
            sb.AppendLine($"Total: {total} | Loaded: {loaded} | Disabled: {disabled} | Failed: {failed}");

            foreach (var mod in _currentLocalModsData)
            {
                string stateIcon = mod.State switch
                {
                    ModLoadState.Loaded => "✓",
                    ModLoadState.Disabled => "-",
                    ModLoadState.Failed => "✗",
                    _ => "?"
                };
                sb.AppendLine($"  [{mod.LoadOrder:D2}] {stateIcon} {mod.Name} v{mod.Version} ({mod.State})");
            }
        }
        else
        {
            sb.AppendLine("(No diff data available)");
        }

        // 复制到系统剪贴板
        DisplayServer.ClipboardSet(sb.ToString().TrimEnd());

        _hintLabel.Text = L.T("CopyDiffSuccess");
        _hintLabel.Modulate = new Color(0.4f, 1f, 0.4f);
    }

    private void HidePanel()
    {
        _isVisible = false;
        Visible = false;
        _mainPanel.Hide();
    }

    /// <summary>
    /// v2.1.2: 关闭面板前保存窗口状态和profile选择
    /// 修复缺陷: ×按钮原直接调HidePanel，跳过SaveWindowState导致profile选择丢失
    /// </summary>
    private void OnCloseClicked()
    {
        SaveWindowState();
        HidePanel();
    }

    private void OnRefreshClicked()
    {
        // v2.1.2: 手动刷新时清除配置选择状态，防止持久化脏路径
        _selectedProfilePath = null;

        var localMods = ModSyncCore.ScanLocalMods();

        // 如果没有远程信息，只显示本地 MOD 概况
        _diffList.ClearChildren();

        var header = CreateDiffRow(L.T("HeaderName"), L.T("HeaderState"), L.T("HeaderVersion"), ColorHeader);
        header.AddThemeConstantOverride("separation", 8);
        _diffList.AddChild(header);

        var separator = new HSeparator();
        separator.Modulate = new Color(0.3f, 0.3f, 0.3f);
        _diffList.AddChild(separator);

        foreach (var mod in localMods)
        {
            Color color = mod.State switch
            {
                ModLoadState.Loaded => new Color(0.4f, 1f, 0.4f),
                ModLoadState.Disabled => new Color(0.5f, 0.5f, 0.5f),
                ModLoadState.Failed => new Color(1f, 0.3f, 0.3f),
                _ => ColorNormal
            };

            var row = CreateDiffRow(
                $"[{mod.LoadOrder:D2}] {mod.Name}",
                mod.State.ToString(),
                mod.Version,
                color
            );
            _diffList.AddChild(row);
        }

        int total = localMods.Count;
        int loaded = localMods.Count(m => m.State == ModLoadState.Loaded);
        int failed = localMods.Count(m => m.State == ModLoadState.Failed);
        int disabled = localMods.Count(m => m.State == ModLoadState.Disabled);

        _summaryLabel.Text = L.TF("SummaryTotal", total, loaded, failed, disabled);
        _summaryLabel.Modulate = failed > 0 ? new Color(1f, 0.4f, 0.4f) : new Color(0.4f, 1f, 0.4f);
        _hintLabel.Text = L.T("HintRefresh");
        _applyOrderBtn.Visible = false;
        _currentProfile = null;
        _deleteProfileBtn.Visible = false;

        // v2.2.0: 存储本地MOD数据供一键复制使用
        _currentDiffData = null;
        _currentLocalModsData = localMods;
    }

    private void DisplayDifferences(List<ModDifference> differences, List<ModInfoSnapshot>? allLocalMods = null)
    {
        // v2.2.0: 存储差异数据供一键复制使用
        _currentDiffData = differences;
        _currentLocalModsData = allLocalMods;

        _diffList.ClearChildren();

        // 表头
        var header = CreateDiffRow(L.T("HeaderName"), L.T("HeaderType"), L.T("HeaderDetails"), ColorHeader);
        _diffList.AddChild(header);

        var separator = new HSeparator();
        separator.Modulate = new Color(0.3f, 0.3f, 0.3f);
        _diffList.AddChild(separator);

        if (differences.Count == 0 && (allLocalMods == null || allLocalMods.Count == 0))
        {
            var noDiffLabel = new Label();
            noDiffLabel.Text = L.T("NoDiff");
            noDiffLabel.HorizontalAlignment = HorizontalAlignment.Center;
            noDiffLabel.AddThemeFontSizeOverride("font_size", ScaledFontSize(16));
            noDiffLabel.Modulate = new Color(0.4f, 1f, 0.4f);
            _diffList.AddChild(noDiffLabel);

            _summaryLabel.Text = L.T("AllSynced");
            _summaryLabel.Modulate = new Color(0.4f, 1f, 0.4f);
            return;
        }

        // 分类统计
        var criticalDiffs = differences.Where(d => d.Type is DifferenceType.MissingOnLocal or DifferenceType.MissingOnRemote or DifferenceType.VersionMismatch or DifferenceType.StateMismatch or DifferenceType.HashMismatch).ToList();
        var orderDiffs = differences.Where(d => d.Type == DifferenceType.OrderMismatch).ToList();
        var extraGameplayDiffs = differences.Where(d => d.Type == DifferenceType.ExtraMod && !d.IsToolMod).ToList();
        var extraToolDiffs = differences.Where(d => d.Type == DifferenceType.ExtraMod && d.IsToolMod).ToList();

        // 1. 关键差异组（影响联机同步）
        if (criticalDiffs.Count > 0)
        {
            var container = AddCollapsibleGroup(L.TF("GroupCritical", criticalDiffs.Count), new Color(1f, 0.4f, 0.4f));
            foreach (var diff in criticalDiffs)
                container.AddChild(CreateDiffRow(diff.Name, GetDiffTypeText(diff.Type), diff.Details, GetDiffColor(diff.Type)));
        }

        // 2. 顺序差异组
        if (orderDiffs.Count > 0)
        {
            var container = AddCollapsibleGroup(L.TF("GroupOrder", orderDiffs.Count), ColorOrderMismatch);
            foreach (var diff in orderDiffs)
                container.AddChild(CreateDiffRow(diff.Name, GetDiffTypeText(diff.Type), diff.Details, GetDiffColor(diff.Type)));
        }

        // 3. 额外MOD组（影响玩法，不在配置中）
        if (extraGameplayDiffs.Count > 0)
        {
            var container = AddCollapsibleGroup(L.TF("GroupExtraGameplay", extraGameplayDiffs.Count), new Color(1f, 0.8f, 0.3f));
            foreach (var diff in extraGameplayDiffs)
                container.AddChild(CreateDiffRow(diff.Name, GetDiffTypeText(diff.Type), diff.Details, GetDiffColor(diff.Type)));
        }

        // 4. 额外工具MOD组（不影响玩法，安全）
        if (extraToolDiffs.Count > 0)
        {
            var container = AddCollapsibleGroup(L.TF("GroupExtraTool", extraToolDiffs.Count), new Color(0.6f, 0.6f, 0.6f));
            foreach (var diff in extraToolDiffs)
                container.AddChild(CreateDiffRow(diff.Name, GetDiffTypeText(diff.Type), diff.Details, GetDiffColor(diff.Type)));
        }

        // 5. 已通过检测的MOD（默认折叠）
        if (allLocalMods != null)
        {
            var diffIds = new HashSet<string>(differences.Select(d => d.Id));
            var passedMods = allLocalMods.Where(m => !diffIds.Contains(m.Id)).ToList();
            if (passedMods.Count > 0)
            {
                var container = AddCollapsibleGroup(L.TF("GroupPassed", passedMods.Count), new Color(0.4f, 1f, 0.4f), expanded: false);
                foreach (var mod in passedMods)
                {
                    string stateText = mod.State switch
                    {
                        ModLoadState.Loaded => L.T("StateLoaded"),
                        ModLoadState.Disabled => L.T("StateDisabled"),
                        ModLoadState.Failed => L.T("StateFailed"),
                        _ => mod.State.ToString()
                    };
                    container.AddChild(CreateDiffRow(mod.Name, L.T("Passed"), L.TF("VersionFmt", mod.Version, stateText), new Color(0.4f, 1f, 0.4f)));
                }
            }
        }

        // 摘要统计
        int missingLocal = differences.Count(d => d.Type == DifferenceType.MissingOnLocal);
        int missingRemote = differences.Count(d => d.Type == DifferenceType.MissingOnRemote);
        int versionDiff = differences.Count(d => d.Type == DifferenceType.VersionMismatch);
        int stateDiff = differences.Count(d => d.Type == DifferenceType.StateMismatch);
        int orderDiff = orderDiffs.Count;
        int extraGameplay = extraGameplayDiffs.Count;
        int extraTool = extraToolDiffs.Count;

        var parts = new List<string>();
        if (missingLocal > 0) parts.Add(L.TF("SummaryMissingLocal", missingLocal));
        if (missingRemote > 0) parts.Add(L.TF("SummaryMissingRemote", missingRemote));
        if (versionDiff > 0) parts.Add(L.TF("SummaryVersion", versionDiff));
        if (stateDiff > 0) parts.Add(L.TF("SummaryState", stateDiff));
        if (orderDiff > 0) parts.Add(L.TF("SummaryOrder", orderDiff));
        if (extraGameplay > 0) parts.Add(L.TF("SummaryExtraGameplay", extraGameplay));
        if (extraTool > 0) parts.Add(L.TF("SummaryExtraTool", extraTool));

        _summaryLabel.Text = parts.Count > 0 ? string.Join(" | ", parts) : L.TF("SummaryDiffCount", differences.Count);
        _summaryLabel.Modulate = new Color(1f, 0.6f, 0.2f);
        _hintLabel.Text = L.T("HintColors");
    }

    private static string GetDiffTypeText(DifferenceType type)
    {
        return type switch
        {
            DifferenceType.MissingOnLocal => L.T("DiffMissingLocal"),
            DifferenceType.MissingOnRemote => L.T("DiffMissingRemote"),
            DifferenceType.VersionMismatch => L.T("DiffVersion"),
            DifferenceType.StateMismatch => L.T("DiffState"),
            DifferenceType.HashMismatch => L.T("DiffHash"),
            DifferenceType.OrderMismatch => L.T("DiffOrder"),
            DifferenceType.ExtraMod => L.T("DiffExtra"),
            _ => L.T("DiffUnknown")
        };
    }

    private static Color GetDiffColor(DifferenceType type)
    {
        return type switch
        {
            DifferenceType.MissingOnLocal => ColorMissingLocal,
            DifferenceType.MissingOnRemote => ColorMissingRemote,
            DifferenceType.VersionMismatch => ColorVersionMismatch,
            DifferenceType.StateMismatch => ColorStateMismatch,
            DifferenceType.HashMismatch => ColorHashMismatch,
            DifferenceType.OrderMismatch => ColorOrderMismatch,
            DifferenceType.ExtraMod => ColorExtraMod,
            _ => ColorNormal
        };
    }

    private VBoxContainer AddCollapsibleGroup(string title, Color color, bool expanded = true)
    {
        var headerBtn = new Button();
        headerBtn.Text = expanded ? $"▼ {title}" : $"▸ {title}";
        headerBtn.Alignment = HorizontalAlignment.Left;
        headerBtn.Flat = true;
        headerBtn.AddThemeFontSizeOverride("font_size", ScaledFontSize(13));
        headerBtn.Modulate = color;
        headerBtn.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.Fill;

        var content = new VBoxContainer();
        content.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
        content.Visible = expanded;

        headerBtn.Pressed += () =>
        {
            content.Visible = !content.Visible;
            headerBtn.Text = content.Visible ? $"▼ {title}" : $"▸ {title}";
        };

        _diffList.AddChild(headerBtn);
        _diffList.AddChild(content);

        return content;
    }

    private static HBoxContainer CreateDiffRow(string col1, string col2, string col3, Color color)
    {
        var row = new HBoxContainer();
        row.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
        row.AddThemeConstantOverride("separation", 8);

        var label1 = new Label();
        label1.Text = col1;
        label1.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
        label1.SizeFlagsStretchRatio = 2f;
        label1.AddThemeFontSizeOverride("font_size", ScaledFontSize(12));
        label1.Modulate = color;
        row.AddChild(label1);

        var label2 = new Label();
        label2.Text = col2;
        label2.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
        label2.SizeFlagsStretchRatio = 1f;
        label2.AddThemeFontSizeOverride("font_size", ScaledFontSize(12));
        label2.Modulate = color;
        row.AddChild(label2);

        var label3 = new Label();
        label3.Text = col3;
        label3.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.Fill;
        label3.SizeFlagsStretchRatio = 2f;
        label3.AddThemeFontSizeOverride("font_size", ScaledFontSize(11));
        label3.Modulate = color;
        row.AddChild(label3);

        return row;
    }

    private void OnExportClicked()
    {
        string path = ModSyncCore.ExportProfile("ModProfile");
        if (!string.IsNullOrEmpty(path))
        {
            string fileName = Path.GetFileName(path);
            _hintLabel.Text = L.TF("ExportSuccessFmt", fileName);
            _hintLabel.Modulate = new Color(0.4f, 1f, 0.4f);
            RefreshProfileList();
        }
        else
        {
            _hintLabel.Text = L.T("ExportFail");
            _hintLabel.Modulate = new Color(1f, 0.3f, 0.3f);
        }
    }

    private void RefreshProfileList(string? filter = null, string? selectFileName = null)
    {
        _profileSelector.Clear();
        var profiles = ModSyncCore.GetExportedProfiles();

        // 过滤不存在的文件（用户可能在外部删除了配置文件）
        profiles = profiles.Where(p => File.Exists(p)).ToList();

        if (!string.IsNullOrWhiteSpace(filter))
        {
            profiles = profiles.Where(p => Path.GetFileName(p).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        _currentProfiles = profiles;

        _profileSelector.AddItem(L.T("SelectProfile"), 0);
        int selectIndex = 0;
        for (int i = 0; i < profiles.Count; i++)
        {
            string fileName = Path.GetFileName(profiles[i]);
            _profileSelector.AddItem(fileName, i + 1);
            if (selectFileName != null && fileName.Equals(selectFileName, StringComparison.OrdinalIgnoreCase))
                selectIndex = i + 1;
        }

        if (selectIndex > 0)
        {
            // v2.1.2: Select() 触发 ItemSelected → OnProfileSelected，移除冗余显式调用
            _profileSelector.Select(selectIndex);
        }
        else
        {
            _deleteProfileBtn.Visible = false;
            _currentProfile = null;
        }
    }

    private void OnImportClicked()
    {
        if (_fileDialog != null && GodotObject.IsInstanceValid(_fileDialog))
            _fileDialog.QueueFree();

        _fileDialog = new FileDialog();
        _fileDialog.FileMode = FileDialog.FileModeEnum.OpenFile;
        _fileDialog.Access = FileDialog.AccessEnum.Filesystem;
        _fileDialog.Filters = new[] { "*.json ; JSON配置文件" };
        _fileDialog.CurrentPath = ModSyncCore.ProfileDir.Replace("\\", "/") + "/";
        _fileDialog.FileSelected += OnImportFileSelected;
        _fileDialog.Canceled += () => _fileDialog?.QueueFree();
        AddChild(_fileDialog);
        _fileDialog.PopupCentered(new Vector2I(800, 600));
    }

    private void OnImportFileSelected(string path)
    {
        try
        {
            string fileName = Path.GetFileName(path);
            string destPath = Path.Combine(ModSyncCore.ProfileDir, fileName);

            // 如果源文件已经在 ProfileDir 内，跳过复制（避免 File.Copy 自身抛异常）
            string sourceDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            string profileDir = Path.GetFullPath(ModSyncCore.ProfileDir);
            if (!sourceDir.Equals(profileDir, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(path, destPath, true);
            }

            _hintLabel.Text = L.TF("ImportSuccessFmt", fileName);
            _hintLabel.Modulate = new Color(0.4f, 1f, 0.4f);

            // 清空搜索框并刷新列表，自动选中新导入的文件
            _searchLineEdit.Text = "";
            RefreshProfileList(selectFileName: fileName);
        }
        catch (Exception ex)
        {
            _hintLabel.Text = L.TF("ImportFailFmt", ex.Message);
            _hintLabel.Modulate = new Color(1f, 0.3f, 0.3f);
        }
        _fileDialog?.QueueFree();
        _fileDialog = null;
    }

    private void OnDeleteProfileClicked()
    {
        int selectedIndex = _profileSelector.Selected;
        if (selectedIndex <= 0 || selectedIndex - 1 >= _currentProfiles.Count)
            return;

        string filePath = _currentProfiles[selectedIndex - 1];
        string fileName = Path.GetFileName(filePath);

        // 创建确认对话框
        _deleteConfirmDialog = new ConfirmationDialog();
        _deleteConfirmDialog.Title = L.T("DeleteConfirmTitle");
        _deleteConfirmDialog.DialogText = L.TF("DeleteConfirmText", fileName);
        _deleteConfirmDialog.GetOkButton().Text = L.T("DeleteConfirmYes");
        _deleteConfirmDialog.GetCancelButton().Text = L.T("DeleteConfirmNo");
        _deleteConfirmDialog.Confirmed += () => ConfirmDeleteProfile(filePath);
        _deleteConfirmDialog.Canceled += () => _deleteConfirmDialog?.QueueFree();
        AddChild(_deleteConfirmDialog);
        _deleteConfirmDialog.PopupCentered();
    }

    private void ConfirmDeleteProfile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                string fileName = Path.GetFileName(filePath);
                _hintLabel.Text = L.TF("DeleteSuccessFmt", fileName);
                _hintLabel.Modulate = new Color(0.4f, 1f, 0.4f);

                // v2.1.2: 删除的是当前选中配置文件时清除追踪，防止持久化脏路径
                if (_selectedProfilePath == filePath)
                    _selectedProfilePath = null;

                RefreshProfileList();
            }
        }
        catch (Exception ex)
        {
            _hintLabel.Text = L.TF("DeleteFailFmt", ex.Message);
            _hintLabel.Modulate = new Color(1f, 0.3f, 0.3f);
        }
        _deleteConfirmDialog?.QueueFree();
        _deleteConfirmDialog = null;
    }

    private void OnSearchTextChanged(string text)
    {
        RefreshProfileList(text);
    }

    private void OnProfileSelectedByPath(string filePath)
    {
        for (int i = 0; i < _currentProfiles.Count; i++)
        {
            if (_currentProfiles[i] == filePath)
            {
                // v2.1.2: Select() 触发 ItemSelected → OnProfileSelected，移除冗余显式调用
                _profileSelector.Select(i + 1); // index 0 is "SelectProfile"
                return;
            }
        }
        // v2.1.2: 路径不在当前列表中（文件被删除/移动），清除追踪防止持久化脏数据
        _selectedProfilePath = null;
    }

    private void OnProfileSelected(long index)
    {
        if (index <= 0)
        {
            _deleteProfileBtn.Visible = false;
            _currentProfile = null;
            _selectedProfilePath = null; // v2.1.2: 用户选择占位项时清除追踪
            return;
        }

        if (index - 1 < _currentProfiles.Count)
        {
            _deleteProfileBtn.Visible = true;
            _selectedProfilePath = _currentProfiles[(int)index - 1]; // v2.1.2: 精确追踪完整路径
            var profile = ModSyncCore.ReadProfile(_selectedProfilePath);
            if (profile != null)
            {
                DisplayProfileComparison(profile);
            }
        }
    }

    private void DisplayProfileComparison(ModProfile profile)
    {
        _currentProfile = profile;
        var localMods = ModSyncCore.ScanLocalMods();
        var differences = new List<ModDifference>();
        var profileDict = profile.Mods.ToDictionary(m => m.Id, m => m);
        var localDict = localMods.ToDictionary(m => m.Id, m => m);

        // 检查配置中有但本地没有的
        foreach (var entry in profile.Mods)
        {
            if (!localDict.ContainsKey(entry.Id))
            {
                differences.Add(new ModDifference
                {
                    Id = entry.Id,
                    Name = entry.Id,
                    Type = DifferenceType.MissingOnLocal,
                    RemoteVersion = entry.Version,
                    Details = L.TF("ProfileRequiredFmt", entry.Version)
                });
            }
            else if (localDict[entry.Id].Version != entry.Version)
            {
                differences.Add(new ModDifference
                {
                    Id = entry.Id,
                    Name = localDict[entry.Id].Name,
                    Type = DifferenceType.VersionMismatch,
                    LocalVersion = localDict[entry.Id].Version,
                    RemoteVersion = entry.Version,
                    Details = L.TF("ProfileVersionFmt", entry.Version, localDict[entry.Id].Version)
                });
            }
        }

        // 检查本地有但配置中没有的 → 标记为 ExtraMod（额外MOD，不影响联机同步）
        foreach (var local in localMods)
        {
            if (!profileDict.ContainsKey(local.Id))
            {
                differences.Add(new ModDifference
                {
                    Id = local.Id,
                    Name = local.Name,
                    Type = DifferenceType.ExtraMod,
                    LocalVersion = local.Version,
                    IsToolMod = !local.AffectsGameplay,
                    Details = local.AffectsGameplay
                        ? L.T("ExtraGameplayDetail")
                        : L.T("ExtraToolDetail")
                });
            }
        }

        // 检测加载顺序差异（仅在 MOD 集合和版本一致时显示顺序问题）
        var orderDiffs = ModSyncCore.CompareLoadOrder(localMods, profile);
        if (orderDiffs.Count > 0)
        {
            differences.AddRange(orderDiffs);
        }

        // 控制"应用排序"按钮可见性：仅在纯顺序差异（无缺失/版本问题/无额外MOD阻塞）时启用
        bool hasBlockingIssues = differences.Any(d => d.Type != DifferenceType.OrderMismatch && d.Type != DifferenceType.ExtraMod);
        bool hasOrderIssues = orderDiffs.Count > 0;
        _applyOrderBtn.Visible = hasOrderIssues && !hasBlockingIssues;

        DisplayDifferences(differences, localMods);
        _hintLabel.Text = L.TF("CompareProfileFmt", profile.ProfileName, profile.CreatedAt);
    }

    // ===== v2.4.1: 编码历史缓存 =====

    /// <summary>
    /// v2.10.5: 将中码加入历史缓存（CodeHistoryManager 去重 + 持久化）
    /// </summary>
    private void AddToCodeHistory(string code)
    {
        string shortLabel = code.Length > 20 ? code[..17] + "..." : code;
        _codeHistory.Add(code, shortLabel);
        RefreshCodeHistoryDropdown();
    }

    /// <summary>
    /// v2.10.5: 刷新编码历史下拉框，从 CodeHistoryManager.Entries 读取
    /// </summary>
    private void RefreshCodeHistoryDropdown()
    {
        if (_codeHistoryDropdown == null) return;
        _codeHistoryDropdown.Clear();
        _codeHistoryDropdown.AddItem(L.T("CodeHistoryPlaceholder"), 0);
        var entries = _codeHistory.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            string label = $"{entries[i].ShortLabel} ({entries[i].Timestamp})";
            _codeHistoryDropdown.AddItem(label, i + 1);
        }
        _codeHistoryDropdown.Select(0);
    }

    /// <summary>
    /// 编码历史下拉框选择事件 — 将选中编码填入输入框
    /// </summary>
    private void OnCodeHistorySelected(long index)
    {
        var entries = _codeHistory.Entries;
        if (index <= 0 || index - 1 >= entries.Count) return;
        _codeInput.Text = entries[(int)index - 1].Code;
        // 重置下拉框到占位项
        _codeHistoryDropdown.Select(0);
    }

    // ===== v2.6.0: 一键同步排序到 setting.save（带备份 + 确认 + 主菜单检测） =====

    /// <summary>
    // v2.10.3: Removed IsInMap() guard — v1.2.3 worked without it.
    // In STS2 the map screen class name is not predictably "NMapScreen".

    private void OnApplyOrderClicked()
    {
        if (_currentProfile == null)
        {
            _hintLabel.Text = L.T("NoProfile");
            _hintLabel.Modulate = new Color(1f, 0.3f, 0.3f);
            return;
        }

        // v2.3.4: Pre-check SaveManager availability before showing confirm dialog
        if (!ModSyncCore.IsSaveManagerAvailable())
        {
            _hintLabel.Text = L.T("ApplyOrderNeedGameStart");
            _hintLabel.Modulate = new Color(1f, 0.6f, 0.2f);
            return;
        }

        // Confirmation dialog
        _applyOrderConfirmDialog = new ConfirmationDialog();
        _applyOrderConfirmDialog.Title = L.T("ApplyOrderConfirmTitle");
        _applyOrderConfirmDialog.DialogText = L.TF("ApplyOrderConfirmText", _currentProfile.ProfileName);
        _applyOrderConfirmDialog.GetOkButton().Text = L.T("ApplyOrderConfirmYes");
        _applyOrderConfirmDialog.GetCancelButton().Text = L.T("ApplyOrderConfirmNo");
        _applyOrderConfirmDialog.Confirmed += ConfirmApplyOrder;
        _applyOrderConfirmDialog.Canceled += () => _applyOrderConfirmDialog?.QueueFree();
        AddChild(_applyOrderConfirmDialog);
        _applyOrderConfirmDialog.PopupCentered();
    }

    private void ConfirmApplyOrder()
    {
        try
        {
            if (_currentProfile == null) return;

            if (ModSyncCore.TryApplyProfileOrder(_currentProfile, out var error))
            {
                _hintLabel.Text = L.T("ApplyOrderSuccess");
                _hintLabel.Modulate = new Color(0.4f, 1f, 0.4f);
                _applyOrderBtn.Visible = false;
            }
            else
            {
                _hintLabel.Text = L.TF("ApplyOrderFailFmt", error);
                _hintLabel.Modulate = new Color(1f, 0.3f, 0.3f);
            }
        }
        catch (Exception ex)
        {
            _hintLabel.Text = L.TF("ApplyOrderFailFmt", ex.Message);
            _hintLabel.Modulate = new Color(1f, 0.3f, 0.3f);
        }
        _applyOrderConfirmDialog?.QueueFree();
        _applyOrderConfirmDialog = null;
    }

    // ===== v2.10.0: 编码排序 — 解析中码的OrderIndex应用到settings.save =====

    private void OnApplyEncodingOrderClicked()
    {
        var code = _codeInput.Text.StripEdges();
        if (string.IsNullOrEmpty(code) || !code.StartsWith("#MSCv2#"))
        {
            _hintLabel.Text = L.T("CodeEmpty");
            _hintLabel.Modulate = new Color(1f, 0.3f, 0.3f);
            return;
        }

        // v2.3.4: Pre-check SaveManager availability before showing confirm dialog
        if (!ModSyncCore.IsSaveManagerAvailable())
        {
            _hintLabel.Text = L.T("ApplyOrderNeedGameStart");
            _hintLabel.Modulate = new Color(1f, 0.6f, 0.2f);
            return;
        }

        // Confirmation dialog
        _applyEncodingOrderConfirmDialog = new ConfirmationDialog();
        _applyEncodingOrderConfirmDialog.Title = L.T("ApplyEncodingOrderConfirmTitle");
        _applyEncodingOrderConfirmDialog.DialogText = L.T("ApplyEncodingOrderConfirmText");
        _applyEncodingOrderConfirmDialog.GetOkButton().Text = L.T("ApplyOrderConfirmYes");
        _applyEncodingOrderConfirmDialog.GetCancelButton().Text = L.T("ApplyOrderConfirmNo");
        _applyEncodingOrderConfirmDialog.Confirmed += ConfirmApplyEncodingOrder;
        _applyEncodingOrderConfirmDialog.Canceled += () => _applyEncodingOrderConfirmDialog?.QueueFree();
        AddChild(_applyEncodingOrderConfirmDialog);
        _applyEncodingOrderConfirmDialog.PopupCentered();
    }

    private void ConfirmApplyEncodingOrder()
    {
        try
        {
            var code = _codeInput.Text.StripEdges();
            if (ModSyncCore.TryApplyEncodingOrder(code, out var error))
            {
                _hintLabel.Text = L.T("ApplyOrderSuccess");
                _hintLabel.Modulate = new Color(0.4f, 1f, 0.4f);
                _applyEncodingOrderBtn.Visible = false;
            }
            else
            {
                _hintLabel.Text = L.TF("ApplyOrderFailFmt", error);
                _hintLabel.Modulate = new Color(1f, 0.3f, 0.3f);
            }
        }
        catch (Exception ex)
        {
            _hintLabel.Text = L.TF("ApplyOrderFailFmt", ex.Message);
            _hintLabel.Modulate = new Color(1f, 0.3f, 0.3f);
        }
        _applyEncodingOrderConfirmDialog?.QueueFree();
        _applyEncodingOrderConfirmDialog = null;
    }
}

/// <summary>
/// 窗口状态数据模型
/// </summary>
public class WindowStateData
{
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public bool IsMaximized { get; set; }
    public bool IsCodeMode { get; set; } // v2.0.0
    public string? LastProfilePath { get; set; } // v2.1.2: 记住最后选择的配置文件
}

/// <summary>
/// 窗口状态持久化管理器
/// </summary>
public class WindowStateManager
{
    private readonly string _filePath;

    public WindowStateManager()
    {
        _filePath = Path.Combine(ModSyncCore.ProfileDir, "..", "window_state.json");
        _filePath = Path.GetFullPath(_filePath);
    }

    public WindowStateData? Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            string json = File.ReadAllText(_filePath);
            return System.Text.Json.JsonSerializer.Deserialize<WindowStateData>(json);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModSyncChecker] Failed to load window state: {ex.Message}");
            return null;
        }
    }

    public void Save(WindowStateData state)
    {
        try
        {
            string json = System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModSyncChecker] Failed to save window state: {ex.Message}");
        }
    }
}

/// <summary>
/// 右下角缩放手柄，绘制三角形指示器
/// </summary>
internal partial class ResizeHandleControl : Control
{
    public override void _Ready()
    {
        MouseDefaultCursorShape = CursorShape.Bdiagsize;
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _Draw()
    {
        var c = new Color(0.6f, 0.6f, 0.6f, 0.8f);
        var s = Size;

        // 绘制双层三角形 ◢，营造层次感
        var points = new Vector2[]
        {
            new Vector2(s.X, s.Y - 10),
            new Vector2(s.X, s.Y),
            new Vector2(s.X - 10, s.Y)
        };
        DrawPolygon(points, new Color[] { c, c, c });

        var c2 = new Color(0.8f, 0.8f, 0.8f, 0.5f);
        var points2 = new Vector2[]
        {
            new Vector2(s.X, s.Y - 6),
            new Vector2(s.X, s.Y),
            new Vector2(s.X - 6, s.Y)
        };
        DrawPolygon(points2, new Color[] { c2, c2, c2 });
    }
}

/// <summary>
/// 自定义背景面板，手动绘制圆角矩形背景，不使用 PanelContainer 避免布局干扰
/// </summary>
internal partial class PanelBackground : Control
{
    private StyleBoxFlat? _style;

    public void SetBackgroundStyle(StyleBoxFlat style)
    {
        _style = style;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_style != null)
        {
            _style.Draw(GetCanvasItem(), new Rect2(Vector2.Zero, Size));
        }
    }
}

/// <summary>
/// VBoxContainer 扩展方法
/// </summary>
public static class VBoxContainerExtensions
{
    public static void ClearChildren(this VBoxContainer container)
    {
        foreach (var child in container.GetChildren())
        {
            child.QueueFree();
        }
    }
}

/// <summary>
/// v2.5.0: TextEdit subclass that detects paste events (Ctrl+V) for auto-encoding detection.
/// </summary>
internal partial class PasteDetectTextEdit : TextEdit
{
    public event Action? Pasted;

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo
            && keyEvent.Keycode == Key.V && keyEvent.CtrlPressed)
        {
            base._GuiInput(@event);
            // Deferred so Text property reflects the pasted content
            CallDeferred("FirePasted");
            return;
        }
        base._GuiInput(@event);
    }

    private void FirePasted() => Pasted?.Invoke();
}
