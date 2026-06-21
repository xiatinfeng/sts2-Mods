using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using ModSyncChecker.Scripts;

namespace ModSyncChecker.Scripts.UI;

/// <summary>
/// v3.0: Full-screen game-native submenu (BaseLib config page style).
/// Left nav: 编码 / 导出导入. Right: content matching user spec layout.
/// K-key floating panel untouched.
/// </summary>
public partial class NModSyncSubmenu : NSubmenu
{
    // ===== Colors (game-native palette) =====
    private static readonly Color C_Bg = new(0.04f, 0.04f, 0.06f);
    private static readonly Color C_NavBg = new(0.06f, 0.06f, 0.08f);
    private static readonly Color C_SectionBg = new(0.06f, 0.06f, 0.09f);
    private static readonly Color C_Accent = new(1f, 0.75f, 0.3f);   // gold
    private static readonly Color C_Text = new(0.85f, 0.85f, 0.85f);
    private static readonly Color C_Dim = new(0.5f, 0.5f, 0.55f);
    private static readonly Color C_Loaded = new(0.4f, 1f, 0.4f);    // green
    private static readonly Color C_Failed = new(1f, 0.3f, 0.3f);    // red
    private static readonly Color C_Disabled = new(0.5f, 0.5f, 0.5f); // gray
    private const float NavW = 160f;

    // ===== UI references =====
    private Control _topBar = null!;
    private PanelContainer _navPanel = null!;
    private Button _btnEncoding = null!;
    private Button _btnImport = null!;
    private Button _btnConfig = null!;
    private Control _encodingView = null!;
    private ScrollContainer _importView = null!;
    private Control _configView = null!;
    private Label _statusLabel = null!;
    private Label _infoLabel = null!;
    private HBoxContainer _bottomBar = null!;
    private HBoxContainer _infoBar = null!;
    private CheckButton _disabledCheck = null!;

    // ===== Import view controls =====
    private OptionButton _profileSelector = null!;
    private LineEdit _searchInput = null!;
    private VBoxContainer _modTable = null!;
    private VBoxContainer? _encModTable;
    private HBoxContainer? _encHeaderRow;
    private Button _deleteProfileBtn = null!;
    private Button _applyOrderBtn = null!;
    private Button? _encApplyOrderBtn;
    private Button _encAlphaBtn = null!;
    private Button _impAlphaBtn = null!;

    // ===== Encoding view controls =====
    private TextEdit _codeInput = null!;
    private OptionButton _historyDropdown = null!;
    private Label _codeResultLabel = null!;

    // ===== Column resizing =====
    // ===== UI layout debug =====
    private int _debugFrameCounter;

    private float _nameColWidth = 320f;
    private bool _isDraggingHandle;
    private float _handleDragStartX;
    private float _handleDragStartColWidth;
    private Control? _colHandle;
    private Label? _headerNameLbl;
    private Label? _encHeaderNameLbl;  // v3.1.1: separate ref for encoding panel header
    private List<ModInfoSnapshot> _localMods = new();
    private List<string> _profilePaths = new();
    private string? _selectedProfilePath;
    private List<ModDifference>? _currentDiffData;
    private List<ModInfoSnapshot>? _currentLocalMods;
    private List<string> _codeHistory = new();
    private List<string>? _hiddenOnLocal; // mods hidden on disk (resolved by ResolveHiddenMods)
    private bool _alphaSort; // toggle alphabetical sort
    private bool _alphaSortPending; // A-Z clicked, waiting for confirm

    protected override Control? InitialFocusedControl => _btnEncoding;

    private enum ViewType { Encoding, Import, Config }

    public NModSyncSubmenu()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        Name = "ModSyncSubmenu";
    }

    public override void _Ready()
    {
        GD.Print("[NModSyncSubmenu._Ready] STEP 1: begin");
        // === Background ===
        var bg = new ColorRect { Color = C_Bg };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        GD.Print("[NModSyncSubmenu._Ready] STEP 2: LoadConfig");
        // Load persisted column width
        var config = ModSyncCore.LoadConfigFromFile();
        if (config != null)
            _nameColWidth = config.ColumnWidth;

        GD.Print("[NModSyncSubmenu._Ready] STEP 3: BuildTopBar");
        BuildTopBar();
        GD.Print("[NModSyncSubmenu._Ready] STEP 4: BuildNav");
        BuildNav();
        GD.Print("[NModSyncSubmenu._Ready] STEP 5: BuildEncodingView");
        BuildEncodingView();
        GD.Print("[NModSyncSubmenu._Ready] STEP 6: BuildImportView");
        BuildImportView();
        GD.Print("[NModSyncSubmenu._Ready] STEP 7: BuildConfigView");
        BuildConfigView();

        GD.Print("[NModSyncSubmenu._Ready] STEP 8: infoBar");
        // ── Info message bar (centered, above status bar) ──
        _infoBar = new HBoxContainer();
        _infoBar.SetAnchorsPreset(LayoutPreset.BottomWide);
        _infoBar.OffsetTop = -(40 + S(14) + 4); // dynamic spacing: status bar + font height
        _infoBar.AddThemeConstantOverride("separation", 8);
        AddChild(_infoBar);

        _infoLabel = new Label();
        _infoLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _infoLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _infoLabel.AddThemeFontSizeOverride("font_size", S(14));
        _infoLabel.AddThemeColorOverride("font_color", C_Accent);
        _infoBar.AddChild(_infoLabel);

        GD.Print("[NModSyncSubmenu._Ready] STEP 9: bottomBar");
        // ── Status bar (centered, bottom) ──
        // v3.1.1: Use fixed-height approach for small-screen (Android) reliability
        _bottomBar = new HBoxContainer();
        _bottomBar.AnchorLeft = 0;
        _bottomBar.AnchorRight = 1;
        _bottomBar.AnchorTop = 1;
        _bottomBar.AnchorBottom = 1;
        _bottomBar.OffsetTop = -Mathf.Max(S(12) + 8, 24); // min 24px height
        _bottomBar.AddThemeConstantOverride("separation", 8);
        AddChild(_bottomBar);

        _statusLabel = new Label();
        _statusLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _statusLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _statusLabel.SizeFlagsVertical = SizeFlags.ExpandFill;
        _statusLabel.VerticalAlignment = VerticalAlignment.Center;
        _statusLabel.AddThemeFontSizeOverride("font_size", Mathf.Max(S(12), 10)); // min 10pt readable
        _bottomBar.AddChild(_statusLabel);

        GD.Print("[NModSyncSubmenu._Ready] STEP 10: backButton");
        // Game-native back button (NSubmenu ConnectSignals auto-wires ESC/B)
        // v3.1.1-beta.4: back_button.tscn may not exist on Android — null check to prevent SIGSEGV
        try
        {
            var backScene = PreloadManager.Cache.GetScene(SceneHelper.GetScenePath("ui/back_button"));
            if (backScene != null)
            {
                var backButton = backScene.Instantiate<NBackButton>();
                backButton.Name = "BackButton";
                AddChild(backButton);
            }
            else
            {
                FileLogger.Warn("[_Ready] back_button.tscn not found in cache — skipping");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[_Ready] Failed to create back button: {ex.Message}");
        }

        GD.Print("[NModSyncSubmenu._Ready] STEP 11: ConnectSignals");
        ConnectSignals();

        GD.Print("[NModSyncSubmenu._Ready] STEP 12: RefreshData");
        try { RefreshData(); }
        catch (Exception ex) { FileLogger.Error($"[_Ready] RefreshData crashed: {ex.Message}"); GD.Print($"[NModSyncSubmenu._Ready] RefreshData ERROR: {ex.Message}"); }
        
        GD.Print("[NModSyncSubmenu._Ready] DONE");
    }

    // ================================================================
    //  TOP BAR
    // ================================================================
    private void BuildTopBar()
    {
        _topBar = new HBoxContainer();
        _topBar.SetAnchorsPreset(LayoutPreset.TopWide);
        _topBar.OffsetBottom = 48;
        _topBar.AddThemeConstantOverride("separation", 12);
        AddChild(_topBar);

        // Title
        var title = new Label();
        title.Text = "ModSyncChecker 联机同步模组";
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.VerticalAlignment = VerticalAlignment.Center;
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.AddThemeColorOverride("font_color", C_Text);
        title.AddThemeFontSizeOverride("font_size", S(18));
        _topBar.AddChild(title);
    }

    // ================================================================
    //  LEFT NAVIGATION
    // ================================================================
    // Helper: static font scale
    private int S(int b) => Mathf.RoundToInt(b * ModSyncCore.Config.StaticFontScale);

    private static OptionButton MakeScaleDropdown(float current, Action<float> onChanged)
    {
        float[] scales = { 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1.0f, 1.1f, 1.2f, 1.3f, 1.4f, 1.5f, 1.6f, 1.7f, 1.8f, 1.9f, 2.0f, 2.2f, 2.4f, 2.6f, 2.8f, 3.0f };
        int def = 5;
        var dd = new OptionButton();
        for (int i = 0; i < scales.Length; i++)
        {
            dd.AddItem($"{scales[i]:F1}x");
            if (Mathf.Abs(scales[i] - current) < 0.05f) def = i;
        }
        dd.Select(def);
        dd.ItemSelected += idx => onChanged(scales[(int)idx]);
        return dd;
    }

    private void RebuildAllSizes()
    {
        // Re-apply static font scale to all elements by refreshing table + bars
        RenderModTable();
        UpdateStatusBar();
        _statusLabel.AddThemeFontSizeOverride("font_size", S(12));
        _infoLabel.AddThemeFontSizeOverride("font_size", S(13));
    }

    private void BuildNav()
    {
        _navPanel = new PanelContainer();
        _navPanel.SetAnchorsPreset(LayoutPreset.LeftWide);
        _navPanel.OffsetRight = NavW;
        _navPanel.OffsetTop = 48;
        _navPanel.OffsetBottom = 0;
        _navPanel.AddThemeStyleboxOverride("panel",
            new StyleBoxFlat { BgColor = C_NavBg });
        AddChild(_navPanel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 2);
        _navPanel.AddChild(vbox);

        // Header
        var navHeader = new Label();
        navHeader.Text = "MOD SYNC";
        navHeader.HorizontalAlignment = HorizontalAlignment.Center;
        navHeader.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        navHeader.AddThemeColorOverride("font_color", C_Dim);
        navHeader.AddThemeFontSizeOverride("font_size", S(12));
        navHeader.CustomMinimumSize = new Vector2(0, 36);
        vbox.AddChild(navHeader);

        vbox.AddChild(MakeSep());

        _btnEncoding = MakeNavBtn("编码", true);
        _btnEncoding.Pressed += () => SwitchView(ViewType.Encoding);
        vbox.AddChild(_btnEncoding);

        _btnImport = MakeNavBtn("导出导入", false);
        _btnImport.Pressed += () => SwitchView(ViewType.Import);
        vbox.AddChild(_btnImport);

        _btnConfig = MakeNavBtn("Mod配置", false);
        _btnConfig.Pressed += () => SwitchView(ViewType.Config);
        vbox.AddChild(_btnConfig);
    }

    // ================================================================
    //  ENCODING VIEW (default)
    // ================================================================
    private void BuildEncodingView()
    {
        _encodingView = new VBoxContainer();
        _encodingView.SetAnchorsPreset(LayoutPreset.FullRect);
        _encodingView.OffsetLeft = NavW + 12;
        _encodingView.OffsetTop = 48;
        _encodingView.OffsetRight = 0;
        _encodingView.OffsetBottom = 0;
        _encodingView.AddThemeConstantOverride("separation", 10);
        AddChild(_encodingView);

        // Hint
        var hint = new Label();
        hint.Text = "粘贴同步编码来对比 MOD 列表";
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.AddThemeColorOverride("font_color", C_Dim);
        hint.AddThemeFontSizeOverride("font_size", S(13));
        _encodingView.AddChild(hint);

        // Code input
        _codeInput = new TextEdit();
        _codeInput.CustomMinimumSize = new Vector2(0, 80);
        _codeInput.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _encodingView.AddChild(_codeInput);

        // History + buttons row
        var codeRow = new HBoxContainer();
        codeRow.AddThemeConstantOverride("separation", 10);
        _encodingView.AddChild(codeRow);

        _historyDropdown = new OptionButton();
        _historyDropdown.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _historyDropdown.AddItem("— 编码历史 —");
        _historyDropdown.ItemSelected += (idx) =>
        {
            if (idx > 0 && idx - 1 < _codeHistory.Count)
                _codeInput.Text = _codeHistory[(int)idx - 1];
        };
        codeRow.AddChild(_historyDropdown);

        var genBtn = new Button();
        genBtn.Text = "生成编码";
        genBtn.AddThemeFontSizeOverride("font_size", S(15));
        genBtn.Pressed += OnGenerateCode;
        codeRow.AddChild(genBtn);

        var cmpBtn = new Button();
        cmpBtn.Text = "对比编码";
        cmpBtn.AddThemeFontSizeOverride("font_size", S(15));
        cmpBtn.Pressed += OnCompareCode;
        codeRow.AddChild(cmpBtn);

        _encApplyOrderBtn = MakeSmallBtn("应用排序");
        _encApplyOrderBtn.Pressed += OnApplyOrder;
        _encApplyOrderBtn.Visible = false;
        codeRow.AddChild(_encApplyOrderBtn);

        _encAlphaBtn = MakeSmallBtn("A-Z");
        _encAlphaBtn.TooltipText = "按字母排序 / 按加载顺序";
        _encAlphaBtn.Pressed += () => { OnAlphaToggle(); RefreshData(); };
        codeRow.AddChild(_encAlphaBtn);

        // IncludeDisabled (per-page)
        var encCb = new CheckButton();
        encCb.Text = "包含禁用MOD";
        encCb.ButtonPressed = ModSyncConfigNode.IncludeDisabledMods;
        encCb.Pressed += () => { ModSyncConfigNode.IncludeDisabledMods = encCb.ButtonPressed; RefreshData(); };
        codeRow.AddChild(encCb);

        // Result
        _codeResultLabel = new Label();
        _codeResultLabel.AutowrapMode = TextServer.AutowrapMode.Word;
        _codeResultLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _codeResultLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _codeResultLabel.AddThemeColorOverride("font_color", C_Text);
        _codeResultLabel.AddThemeFontSizeOverride("font_size", S(13));
        _encodingView.AddChild(_codeResultLabel);

        // MOD table for encoding view
        _encodingView.AddChild(MakeSep());
        var encModSection = new VBoxContainer();
        encModSection.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        encModSection.SizeFlagsVertical = SizeFlags.ExpandFill; // v3.1.1-beta.14: expand to fill remaining space
        _encodingView.AddChild(encModSection);

        var encHeaderRow = new HBoxContainer();
        encHeaderRow.AddThemeConstantOverride("separation", 8);
        encModSection.AddChild(encHeaderRow);

        var encHName = new Label();
        encHName.Text = "名称";
        encHName.CustomMinimumSize = new Vector2(_nameColWidth, 0);
        _headerNameLbl = encHName;
        _encHeaderNameLbl = encHName;  // v3.1.1: store separately for encoding panel
        encHName.AddThemeColorOverride("font_color", C_Accent);
        encHName.AddThemeFontSizeOverride("font_size", S(14));
        encHeaderRow.AddChild(encHName);

        var encHandle = MakeColHandle();
        encHeaderRow.AddChild(encHandle);

        var encHState = new Label();
        encHState.Text = "状态";
        encHState.HorizontalAlignment = HorizontalAlignment.Center;
        encHState.CustomMinimumSize = new Vector2(70, 0);
        encHState.AddThemeColorOverride("font_color", C_Accent);
        encHState.AddThemeFontSizeOverride("font_size", S(14));
        encHeaderRow.AddChild(encHState);

        var encHVer = new Label();
        encHVer.Text = "版本";
        encHVer.HorizontalAlignment = HorizontalAlignment.Center;
        encHVer.CustomMinimumSize = new Vector2(60, 0);
        encHVer.AddThemeColorOverride("font_color", C_Accent);
        encHVer.AddThemeFontSizeOverride("font_size", S(14));
        encHeaderRow.AddChild(encHVer);

        encModSection.AddChild(MakeSep());

        // v3.1.1-beta.14: wrap mod table in ScrollContainer
        var encScroll = new ScrollContainer();
        encScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        encScroll.VerticalScrollMode = ScrollContainer.ScrollMode.Auto;
        encScroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        encScroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        encModSection.AddChild(encScroll);

        _encModTable = new VBoxContainer();
        _encModTable.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _encModTable.SizeFlagsVertical = SizeFlags.ExpandFill;
        _encModTable.AddThemeConstantOverride("separation", 2);
        encScroll.AddChild(_encModTable);
    }

    // ================================================================
    //  IMPORT / EXPORT VIEW
    // ================================================================
    private void BuildImportView()
    {
        _importView = new ScrollContainer();
        _importView.SetAnchorsPreset(LayoutPreset.FullRect);
        _importView.OffsetLeft = NavW + 12;
        _importView.OffsetTop = 48;
        _importView.OffsetRight = 0;
        _importView.OffsetBottom = 0;
        _importView.Visible = false;
        _importView.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        AddChild(_importView);

        var inner = new VBoxContainer();
        inner.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        inner.AddThemeConstantOverride("separation", 12);
        _importView.AddChild(inner);

        // ── 操作工具栏 ──
        inner.AddChild(MakeSection("操作工具栏", out var toolBar));
        toolBar.AddChild(MakeBtn("刷新检测", OnRefresh));
        toolBar.AddChild(MakeBtn("导出配置", OnExport));
        toolBar.AddChild(MakeBtn("导入配置", OnImport));
        toolBar.AddChild(MakeBtn("复制差异", OnCopyDiff));
        _applyOrderBtn = MakeBtn("应用排序", OnApplyOrder);
        _applyOrderBtn.Visible = false;
        toolBar.AddChild(_applyOrderBtn);

        _impAlphaBtn = MakeBtn("A-Z", () => { OnAlphaToggle(); RenderModTable(); });
        _impAlphaBtn.TooltipText = "按字母排序 / 按加载顺序";
        toolBar.AddChild(_impAlphaBtn);

        // ── 筛选与配置 ──
        inner.AddChild(MakeSection("筛选与配置", out var filterBox));

        var filterRow1 = new HBoxContainer();
        filterRow1.AddThemeConstantOverride("separation", 8);
        filterBox.AddChild(filterRow1);

        var profileLbl = new Label();
        profileLbl.Text = "配置文件:";
        profileLbl.AddThemeColorOverride("font_color", C_Dim);
        profileLbl.AddThemeFontSizeOverride("font_size", S(13));
        filterRow1.AddChild(profileLbl);

        _profileSelector = new OptionButton();
        _profileSelector.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _profileSelector.AddItem("— 选择一个配置文件 —");
        _profileSelector.ItemSelected += OnProfileSelected;
        filterRow1.AddChild(_profileSelector);

        _deleteProfileBtn = MakeSmallBtn("🗑");
        _deleteProfileBtn.Pressed += OnDeleteProfile;
        _deleteProfileBtn.Visible = false;
        filterRow1.AddChild(_deleteProfileBtn);

        var filterRow2 = new HBoxContainer();
        filterRow2.AddThemeConstantOverride("separation", 12);
        filterBox.AddChild(filterRow2);

        var searchLbl = new Label();
        searchLbl.Text = "搜索:";
        searchLbl.AddThemeColorOverride("font_color", C_Dim);
        searchLbl.AddThemeFontSizeOverride("font_size", S(13));
        filterRow2.AddChild(searchLbl);

        _searchInput = new LineEdit();
        _searchInput.PlaceholderText = "输入 MOD 名称过滤...";
        _searchInput.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _searchInput.TextChanged += (_) => RenderModTable();
        filterRow2.AddChild(_searchInput);

        _disabledCheck = new CheckButton();
        _disabledCheck.Text = "包含禁用MOD";
        _disabledCheck.ButtonPressed = ModSyncConfigNode.IncludeDisabledMods;
        _disabledCheck.Pressed += () => { ModSyncConfigNode.IncludeDisabledMods = _disabledCheck.ButtonPressed; RefreshData(); };
        filterRow2.AddChild(_disabledCheck);

        // ── MOD 列表详情 ──
        inner.AddChild(MakeSection("MOD 列表详情", out var modSection));

        // Table header
        var headerRow = new HBoxContainer();
        headerRow.AddThemeConstantOverride("separation", 8);
        modSection.AddChild(headerRow);

        var hName = new Label();
        hName.Text = "名称";
        hName.CustomMinimumSize = new Vector2(_nameColWidth, 0);
        _headerNameLbl = hName;
        hName.AddThemeColorOverride("font_color", C_Accent);
        hName.AddThemeFontSizeOverride("font_size", S(14));
        headerRow.AddChild(hName);

        // Column resize handle
        _colHandle = MakeColHandle();
        headerRow.AddChild(_colHandle);

        var hState = new Label();
        hState.Text = "状态";
        hState.HorizontalAlignment = HorizontalAlignment.Center;
        hState.CustomMinimumSize = new Vector2(70, 0);
        hState.AddThemeColorOverride("font_color", C_Accent);
        hState.AddThemeFontSizeOverride("font_size", S(13));
        headerRow.AddChild(hState);

        var hVer = new Label();
        hVer.Text = "版本";
        hVer.HorizontalAlignment = HorizontalAlignment.Center;
        hVer.CustomMinimumSize = new Vector2(60, 0);
        hVer.AddThemeColorOverride("font_color", C_Accent);
        hVer.AddThemeFontSizeOverride("font_size", S(13));
        headerRow.AddChild(hVer);

        modSection.AddChild(MakeSep());

        _modTable = new VBoxContainer();
        _modTable.AddThemeConstantOverride("separation", 2);
        modSection.AddChild(_modTable);
    }

    // ================================================================
    //  MOD CONFIG VIEW
    // ================================================================
    private void BuildConfigView()
    {
        _configView = new VBoxContainer();
        _configView.SetAnchorsPreset(LayoutPreset.FullRect);
        _configView.OffsetLeft = NavW + 12;
        _configView.OffsetTop = 48;
        _configView.OffsetRight = 0;
        _configView.OffsetBottom = 0;
        _configView.Visible = false;
        _configView.AddThemeConstantOverride("separation", 16);
        AddChild(_configView);

        var title = new Label();
        title.Text = "Mod 配置";
        title.AddThemeColorOverride("font_color", C_Accent);
        title.AddThemeFontSizeOverride("font_size", S(20));
        _configView.AddChild(title);
        _configView.AddChild(MakeSep());

        // Font scale
        var fontBox = new HBoxContainer();
        fontBox.AddThemeConstantOverride("separation", 10);
        _configView.AddChild(fontBox);

        var fontLbl = new Label();
        fontLbl.Text = "浮动面板字体缩放:";
        fontLbl.AddThemeColorOverride("font_color", C_Text);
        fontLbl.AddThemeFontSizeOverride("font_size", S(14));
        fontBox.AddChild(fontLbl);

        var fontDropdown = MakeScaleDropdown(ModSyncCore.Config.FontScale, v => { ModSyncCore.UpdateFontScale(v); SetInfoMessage($"浮动面板字体: {v:F1}x"); });
        fontBox.AddChild(fontDropdown);

        // ── Static panel font scale ──
        var staticBox = new HBoxContainer();
        staticBox.AddThemeConstantOverride("separation", 10);
        _configView.AddChild(staticBox);

        var sLbl = new Label();
        sLbl.Text = "静态面板字体缩放:";
        sLbl.AddThemeColorOverride("font_color", C_Text);
        sLbl.AddThemeFontSizeOverride("font_size", S(14));
        staticBox.AddChild(sLbl);

        var sDropdown = MakeScaleDropdown(ModSyncCore.Config.StaticFontScale, v => { ModSyncCore.UpdateStaticFontScale(v); SetInfoMessage($"静态面板字体: {v:F1}x"); RebuildAllSizes(); });
        staticBox.AddChild(sDropdown);

        // ── Handle width ──
        var handleBox = new HBoxContainer();
        handleBox.AddThemeConstantOverride("separation", 10);
        _configView.AddChild(handleBox);

        var hwLbl = new Label();
        hwLbl.Text = "分隔条宽度:";
        hwLbl.AddThemeColorOverride("font_color", C_Text);
        hwLbl.AddThemeFontSizeOverride("font_size", S(14));
        handleBox.AddChild(hwLbl);

        var hwSlider = new HSlider();
        var hwVal = new Label();
        hwVal.Text = $"{ModSyncCore.Config.HandleWidth:F0}px";
        hwVal.AddThemeColorOverride("font_color", C_Dim);
        hwVal.AddThemeFontSizeOverride("font_size", S(12));

        hwSlider.MinValue = 2;
        hwSlider.MaxValue = 30;
        hwSlider.Step = 1;
        hwSlider.Value = ModSyncCore.Config.HandleWidth;
        hwSlider.CustomMinimumSize = new Vector2(150, 0);
        hwSlider.ValueChanged += (v) =>
        {
            ModSyncCore.UpdateHandleWidth((float)v);
            hwVal.Text = $"{(float)v:F0}px";
            SetInfoMessage($"分隔条宽度: {v:F0}px (重启游戏生效)");
        };
        handleBox.AddChild(hwSlider);
        handleBox.AddChild(hwVal);

        _configView.AddChild(MakeSep());

        // Default panel
        var panelBox = new HBoxContainer();
        panelBox.AddThemeConstantOverride("separation", 10);
        _configView.AddChild(panelBox);

        var panelLbl = new Label();
        panelLbl.Text = "默认面板:";
        panelLbl.AddThemeColorOverride("font_color", C_Text);
        panelLbl.AddThemeFontSizeOverride("font_size", S(14));
        panelBox.AddChild(panelLbl);

        var panelDropdown = new OptionButton();
        panelDropdown.AddItem("编码");
        panelDropdown.AddItem("导出导入");
        panelDropdown.Select(ModSyncCore.Config.DefaultPanel == "encoding" ? 0 : 1);
        panelDropdown.ItemSelected += (idx) =>
        {
            ModSyncCore.UpdateDefaultPanel(idx == 0 ? "encoding" : "import");
            SetInfoMessage($"默认面板: {(idx == 0 ? "编码" : "导出导入")}");
        };
        panelBox.AddChild(panelDropdown);

        _configView.AddChild(MakeSep());

        // ExportEnabledOnly
        var expBox = new HBoxContainer(); expBox.AddThemeConstantOverride("separation", 10);
        _configView.AddChild(expBox);
        var expLbl = new Label(); expLbl.Text = "仅导出已激活MOD:"; expLbl.AddThemeColorOverride("font_color", C_Text); expLbl.AddThemeFontSizeOverride("font_size", S(14));
        expBox.AddChild(expLbl);
        var expCheck = new CheckButton(); expCheck.Text = "是"; expCheck.ButtonPressed = ModSyncCore.Config.ExportEnabledOnly;
        expCheck.Pressed += () => ModSyncCore.UpdateExportEnabledOnly(expCheck.ButtonPressed);
        expBox.AddChild(expCheck);

        _configView.AddChild(MakeSep());

        var note = new Label();
        note.Text = "☑ 包含禁用MOD → 编码/导出导入页面各自管理";
        note.AddThemeColorOverride("font_color", C_Dim);
        note.AddThemeFontSizeOverride("font_size", S(13));
        _configView.AddChild(note);
    }

    // ================================================================
    //  DATA & REFRESH
    // ================================================================
    private void RefreshData()
    {
        GD.Print("[NModSyncSubmenu.RefreshData] ENTER");
        try
        {
            GD.Print("[NModSyncSubmenu.RefreshData] CALLING GetModsForSync...");
            _localMods = ModSyncCore.GetModsForSync();
            GD.Print($"[NModSyncSubmenu.RefreshData] GetModsForSync OK: {_localMods.Count} mods");
        }
        catch (Exception ex)
        {
            GD.Print($"[NModSyncSubmenu.RefreshData] GetModsForSync ERROR: {ex}");
            _localMods = new List<ModInfoSnapshot>();
        }
        GD.Print("[NModSyncSubmenu.RefreshData] AFTER GetModsForSync");
        try
        {
            GD.Print("[NModSyncSubmenu.RefreshData] CALLING GetExportedProfiles...");
            _profilePaths = ModSyncCore.GetExportedProfiles();
            GD.Print($"[NModSyncSubmenu.RefreshData] GetExportedProfiles OK: {_profilePaths.Count} profiles");
        }
        catch (Exception ex)
        {
            GD.Print($"[NModSyncSubmenu.RefreshData] GetExportedProfiles ERROR: {ex}");
            _profilePaths = new List<string>();
        }
        GD.Print("[NModSyncSubmenu.RefreshData] AFTER GetExportedProfiles");
        _currentDiffData = null;
        _currentLocalMods = null;
        GD.Print("[NModSyncSubmenu.RefreshData] STEP R1: RefreshProfileDropdown");
        try { RefreshProfileDropdown(); } catch (Exception ex) { GD.Print($"[NModSyncSubmenu.RefreshData] RefreshProfileDropdown ERROR: {ex.Message}"); }
        GD.Print("[NModSyncSubmenu.RefreshData] STEP R2: RenderModTable");
        try { RenderModTable(); } catch (Exception ex) { GD.Print($"[NModSyncSubmenu.RefreshData] RenderModTable ERROR: {ex.Message}"); }
        GD.Print("[NModSyncSubmenu.RefreshData] STEP R3: UpdateStatusBar");
        try { UpdateStatusBar(); } catch (Exception ex) { GD.Print($"[NModSyncSubmenu.RefreshData] UpdateStatusBar ERROR: {ex.Message}"); }
        GD.Print("[NModSyncSubmenu.RefreshData] DONE");
    }

    private void RefreshProfileDropdown()
    {
        _profileSelector.Clear();
        _profileSelector.AddItem("— 选择一个配置文件 —");
        foreach (var path in _profilePaths)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            _profileSelector.AddItem(name);
        }
    }

    // ================================================================
    //  COMPARE TABLE (grouped diff display)
    // ================================================================
    private void RenderCompareTable()
    {
        if (_currentDiffData == null || _currentLocalMods == null) return;
        SetInfoMessage(_currentDiffData.Count == 0 ? "所有 MOD 一致" : $"共 {_currentDiffData.Count} 处差异");
        // Show "应用排序" when there are order diffs, OR A-Z sort is pending confirm
        var vis = _alphaSortPending || _currentDiffData.Any(d => d.Type == DifferenceType.OrderMismatch);
        _applyOrderBtn.Visible = vis;
        if (_encApplyOrderBtn != null) _encApplyOrderBtn.Visible = vis;
        RenderDiffsInto(_encModTable);
        RenderDiffsInto(_modTable);
    }

    private void RenderDiffsInto(VBoxContainer? table)
    {
        if (table == null) return;
        ModSyncPanel.DisplayDifferencesStatic(_currentDiffData!, _currentLocalMods, table, ModSyncCore.Config.FontScale, _hiddenOnLocal);
    }

    private void RenderDiffs(VBoxContainer? t, List<ModDifference> c, List<ModDifference> o, List<ModDifference> eg, List<ModDifference> et, List<ModInfoSnapshot> p)
    {
        if (t == null) return;
        t.ClearChildren();
        if (c.Count + o.Count + eg.Count + et.Count + p.Count == 0) {
            var ok = new Label { Text = "所有MOD一致", HorizontalAlignment = HorizontalAlignment.Center };
            ok.AddThemeColorOverride("font_color", new Color(0.4f,1f,0.4f)); ok.AddThemeFontSizeOverride("font_size",16);
            t.AddChild(ok); return;
        }
        var hdr = new HBoxContainer(); hdr.AddThemeConstantOverride("separation",8);
        AddCol2(hdr,"MOD名称",new Color(1f,0.75f,0.3f),2f); AddCol2(hdr,"差异类型",new Color(1f,0.75f,0.3f),1f); AddCol2(hdr,"详情",new Color(1f,0.75f,0.3f),1f);
        t.AddChild(hdr); t.AddChild(MakeSep());
        if (c.Count>0) AddDiffG2(t,"关键差异（影响联机）- "+c.Count+" 个", new Color(1f,0.3f,0.3f), c);
        if (o.Count>0) AddDiffG2(t,"顺序差异 - "+o.Count+" 个", new Color(0.3f,0.6f,1f), o);
        if (eg.Count>0) AddDiffG2(t,"额外MOD（影响玩法）- "+eg.Count+" 个", new Color(1f,0.8f,0.3f), eg);
        if (et.Count>0) AddDiffG2(t,"额外工具MOD - "+et.Count+" 个", new Color(0.5f,0.5f,0.5f), et);
        if (p.Count>0) AddPassedG(t,"已通过检测 - "+p.Count+" 个", p);
    }

    private void AddDiffG2(VBoxContainer t, string title, Color clr, List<ModDifference> diffs)
    {
        var content = new VBoxContainer();
        var btn = new Button(); btn.Text = "▼ "+title; btn.Flat=true; btn.Alignment=HorizontalAlignment.Left;
        btn.AddThemeColorOverride("font_color",clr); btn.AddThemeFontSizeOverride("font_size",13);
        btn.Pressed += () => { content.Visible=!content.Visible; btn.Text=(content.Visible?"▼ ":"▸ ")+title; };
        t.AddChild(btn); t.AddChild(content);
        foreach (var d in diffs) {
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation",8);
            var info = d.Details;
            if (d.Type==DifferenceType.VersionMismatch) info=$"本地:{d.LocalVersion}→远程:{d.RemoteVersion}";
            if (d.IsToolMod) info+=" [工具]";
            AddCol2(row,d.Name,clr,2f); AddCol2(row,DN2(d.Type),clr,1f); AddCol2(row,info,clr,1f);
            content.AddChild(row);
        }
    }

    private void AddPassedG(VBoxContainer t, string title, List<ModInfoSnapshot> mods)
    {
        var clr = new Color(0.4f,1f,0.4f);
        var content = new VBoxContainer(); content.Visible=false;
        var btn = new Button(); btn.Text = "▸ "+title; btn.Flat=true; btn.Alignment=HorizontalAlignment.Left;
        btn.AddThemeColorOverride("font_color",clr); btn.AddThemeFontSizeOverride("font_size",14);
        btn.Pressed += () => { content.Visible=!content.Visible; btn.Text=(content.Visible?"▼ ":"▸ ")+title; };
        t.AddChild(btn); t.AddChild(content);
        foreach (var m in mods) {
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation",8);
            AddCol2(row,$"[{m.LoadOrder:D2}] {m.Name}",clr,2f); AddCol2(row,"已通过",clr,1f); AddCol2(row,m.Version,clr,1f);
            content.AddChild(row);
        }
    }

    private void AddDiffG(VBoxContainer panel, string title, Color color, List<ModDifference> diffs)
    {
        var content = new VBoxContainer(); content.Visible = true;
        var btn = new Button(); btn.Text = "▼ " + title; btn.Flat = true; btn.Alignment = HorizontalAlignment.Left;
        btn.AddThemeColorOverride("font_color", color); btn.AddThemeFontSizeOverride("font_size", S(13));
        btn.Pressed += () => { content.Visible = !content.Visible; btn.Text = (content.Visible ? "▼ " : "▸ ") + title; };
        panel.AddChild(btn); panel.AddChild(content);
        foreach (var d in diffs)
        {
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 8);
            var info = d.Details;
            if (d.Type == DifferenceType.VersionMismatch) info = $"本地:{d.LocalVersion}→远程:{d.RemoteVersion}";
            if (d.Type == DifferenceType.ExtraMod && d.IsToolMod) info += " [工具]";
            AddCol2(row, d.Name, color, 2f); AddCol2(row, DN2(d.Type), color, 1f);
            AddCol2(row, info, color, 1f);
            content.AddChild(row);
        }
    }

    private void RenderCompareInto(VBoxContainer? table, List<ModDifference> c, List<ModDifference> o, List<ModDifference> eg, List<ModDifference> et, List<ModInfoSnapshot> passed)
    {
        if (table == null) return;
        table.ClearChildren();
        var gold = new Color(1f, 0.75f, 0.3f);
        var red = new Color(1f, 0.3f, 0.3f);
        var blue = new Color(0.3f, 0.6f, 1f);
        var yellow = new Color(1f, 0.8f, 0.3f);
        var gray = new Color(0.5f, 0.5f, 0.5f);
        var green = new Color(0.4f, 1f, 0.4f);

        // Header
        var hdr = new HBoxContainer(); hdr.AddThemeConstantOverride("separation", 8);
        AddCol2(hdr, "MOD名称", gold, 2f); AddCol2(hdr, "差异类型", gold, 1f); AddCol2(hdr, "详情", gold, 1f);
        table.AddChild(hdr); table.AddChild(MakeSep());

        if (c.Count > 0) AddGroup2(table, "关键差异", red, c);
        if (o.Count > 0) AddGroup2(table, "顺序差异", blue, o);
        if (eg.Count > 0) AddGroup2(table, "额外MOD（影响玩法）", yellow, eg);
        if (et.Count > 0) AddGroup2(table, "额外工具MOD", gray, et);
        if (passed.Count > 0) AddPassedGroup2(table, "已通过检测", green, passed);
    }

    private void AddGroup2(VBoxContainer table, string title, Color color, List<ModDifference> diffs)
    {
        var content = new VBoxContainer(); content.Visible = true;
        var btn = new Button(); btn.Text = $"▼ {title} - {diffs.Count} 个"; btn.Flat = true; btn.Alignment = HorizontalAlignment.Left;
        btn.AddThemeColorOverride("font_color", color); btn.AddThemeFontSizeOverride("font_size", S(14));
        btn.Pressed += () => { content.Visible = !content.Visible; btn.Text = (content.Visible ? "▼ " : "▸ ") + $"{title} - {diffs.Count} 个"; };
        table.AddChild(btn); table.AddChild(content);
        foreach (var d in diffs)
        {
            var info = d.Details;
            if (d.Type == DifferenceType.VersionMismatch) info = $"本地:{d.LocalVersion} → 远程:{d.RemoteVersion}";
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 8);
            AddCol2(row, d.Name, color, 2f); AddCol2(row, DN2(d.Type), color, 1f); AddCol2(row, info, color, 1f);
            content.AddChild(row);
        }
    }

    private void AddPassedGroup2(VBoxContainer table, string title, Color color, List<ModInfoSnapshot> mods)
    {
        var content = new VBoxContainer(); content.Visible = false;
        var btn = new Button(); btn.Text = $"▸ {title} - {mods.Count} 个"; btn.Flat = true; btn.Alignment = HorizontalAlignment.Left;
        btn.AddThemeColorOverride("font_color", color); btn.AddThemeFontSizeOverride("font_size", S(14));
        btn.Pressed += () => { content.Visible = !content.Visible; btn.Text = (content.Visible ? "▼ " : "▸ ") + $"{title} - {mods.Count} 个"; };
        table.AddChild(btn); table.AddChild(content);
        foreach (var m in mods)
        {
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 8);
            AddCol2(row, $"[{m.LoadOrder:D2}] {m.Name}", color, 2f); AddCol2(row, "已通过", color, 1f); AddCol2(row, m.Version, color, 1f);
            content.AddChild(row);
        }
    }

    private static string DN2(DifferenceType t) => t switch
    {
        DifferenceType.MissingOnLocal => "缺少", DifferenceType.MissingOnRemote => "多余",
        DifferenceType.VersionMismatch => "版本不同", DifferenceType.StateMismatch => "状态不同",
        DifferenceType.HashMismatch => "哈希不同", DifferenceType.OrderMismatch => "顺序不同",
        DifferenceType.ExtraMod => "额外", _ => t.ToString()
    };

    private void AddCol2(HBoxContainer row, string text, Color color, float ratio)
    {
        var lbl = new Label(); lbl.Text = text; lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lbl.SizeFlagsStretchRatio = ratio; lbl.AddThemeColorOverride("font_color", color);
        lbl.AddThemeFontSizeOverride("font_size", S(13)); row.AddChild(lbl);
    }

    private void RenderModTable()
    {
        if (_currentDiffData != null)
        {
            RenderCompareTable();
            return;
        }
        RenderModTableInto(_encModTable);
        RenderModTableInto(_modTable);
    }

    private void RenderModTableInto(VBoxContainer? table)
    {
        if (table == null) return;
        table.ClearChildren();
        var filter = (_searchInput?.Text ?? "").Trim().ToLowerInvariant();
        var diffDict = _currentDiffData?.ToDictionary(d => d.Id, d => d);

        var filtered = _localMods.Where(m =>
        {
            if (string.IsNullOrEmpty(filter)) return true;
            return m.Name.ToLowerInvariant().Contains(filter) ||
                   m.Id.ToLowerInvariant().Contains(filter);
        });

        var sorted = _alphaSort
            ? filtered.OrderBy(m => m.Name)
            : filtered.OrderBy(m => m.LoadOrder);

        foreach (var mod in sorted)
        {
            ModDifference? diff = diffDict != null && diffDict.TryGetValue(mod.Id, out var d) ? d : null;

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);

            var stateColor = mod.State switch
            {
                ModLoadState.Loaded => C_Loaded,
                ModLoadState.Failed => C_Failed,
                ModLoadState.Disabled => C_Disabled,
                _ => C_Dim
            };
            var stateText = mod.State.ToString();
            var verText = mod.Version;
            // Override with diff info if available
            if (diff != null)
            {
                stateColor = diff.Type switch
                {
                    DifferenceType.MissingOnLocal => new Color(1f,0.3f,0.3f),
                    DifferenceType.MissingOnRemote => new Color(1f,0.8f,0.2f),
                    DifferenceType.VersionMismatch => new Color(1f,0.5f,0f),
                    DifferenceType.StateMismatch => new Color(0.8f,0.4f,0.8f),
                    DifferenceType.OrderMismatch => new Color(0.3f,0.6f,1f),
                    DifferenceType.ExtraMod => new Color(0.5f,0.5f,0.5f),
                    _ => C_Dim
                };
                stateText = DN2(diff.Type);
                if (diff.Type == DifferenceType.VersionMismatch)
                    verText = $"本地:{mod.Version}→远程:{diff.RemoteVersion}";
                else if (diff.Type == DifferenceType.MissingOnLocal)
                    verText = diff.RemoteVersion ?? "?";
            }

            var nameLbl = new Label();
            nameLbl.Text = $"[{mod.LoadOrder:D2}] {mod.Name}";
            nameLbl.CustomMinimumSize = new Vector2(_nameColWidth, 0);
            nameLbl.AddThemeColorOverride("font_color", mod.IsEnabled ? C_Text : C_Disabled);
            nameLbl.AddThemeFontSizeOverride("font_size", S(26));
            nameLbl.ClipText = true;
            row.AddChild(nameLbl);

            var stateLbl = new Label();
            stateLbl.Text = stateText;
            stateLbl.CustomMinimumSize = new Vector2(70, 0);
            stateLbl.HorizontalAlignment = HorizontalAlignment.Center;
            stateLbl.AddThemeColorOverride("font_color", stateColor);
            stateLbl.AddThemeFontSizeOverride("font_size", S(13));
            row.AddChild(stateLbl);

            var verLbl = new Label();
            verLbl.Text = verText;
            verLbl.CustomMinimumSize = new Vector2(60, 0);
            verLbl.HorizontalAlignment = HorizontalAlignment.Center;
            verLbl.AddThemeColorOverride("font_color", C_Dim);
            verLbl.AddThemeFontSizeOverride("font_size", S(13));
            row.AddChild(verLbl);

            table.AddChild(row);
        }
    }

    private void UpdateStatusBar()
    {
        int total = _localMods.Count;
        int loaded = _localMods.Count(m => m.State == ModLoadState.Loaded);
        int failed = _localMods.Count(m => m.State == ModLoadState.Failed);
        int disabled = _localMods.Count(m => m.State == ModLoadState.Disabled);

        if (_statusLabel != null)
        {
            _statusLabel.Text = $"总计: {total} | 正常: {loaded} | 失败: {failed} | 禁用: {disabled}";
            _statusLabel.AddThemeColorOverride("font_color", failed > 0 ? C_Failed : C_Dim);
        }
    }

    private void SetInfoMessage(string msg)
    {
        if (_infoLabel != null)
            _infoLabel.Text = msg;
    }

    // ================================================================
    //  NAV SWITCHING
    // ================================================================
    private void SwitchView(ViewType view)
    {
        FileLogger.Info($"[NModSyncSubmenu] SwitchView: {view}");
        _encodingView.Visible = view == ViewType.Encoding;
        _importView.Visible = view == ViewType.Import;
        _configView.Visible = view == ViewType.Config;

        SetNavActive(_btnEncoding, view == ViewType.Encoding);
        SetNavActive(_btnImport, view == ViewType.Import);
        SetNavActive(_btnConfig, view == ViewType.Config);

        if (view != ViewType.Encoding) RefreshData();
    }

    private void SetNavActive(Button btn, bool active)
    {
        btn.AddThemeColorOverride("font_color", active ? C_Text : C_Dim);
        btn.AddThemeStyleboxOverride("normal", new StyleBoxFlat
        {
            BgColor = active ? new Color(0.12f, 0.12f, 0.18f) : new Color(0, 0, 0, 0),
            BorderWidthLeft = active ? 4 : 0,
            BorderColor = C_Accent,
            ContentMarginLeft = 14, ContentMarginTop = 11,
            ContentMarginRight = 14, ContentMarginBottom = 11,
        });
    }

    // ================================================================
    //  BUTTON HANDLERS
    // ================================================================
    private void OnGenerateCode()
    {
        FileLogger.Info("[NModSyncSubmenu] OnGenerateCode");
        var mods = ModSyncCore.GetModsForSync();
        if (ModSyncCore.Config.ExportEnabledOnly)
            mods = mods.Where(m => m.IsEnabled).ToList();
        var code = GenerateSyncCode(mods);
        _codeInput.Text = code;
        _codeResultLabel.Text = "编码已生成并复制到剪贴板";
        DisplayServer.ClipboardSet(code);
        FileLogger.Info($"[NModSyncSubmenu] Generated code for {mods.Count} mods (ExportEnabledOnly={ModSyncCore.Config.ExportEnabledOnly})");
        // Save to history
        if (!_codeHistory.Contains(code))
        {
            _codeHistory.Insert(0, code);
            if (_codeHistory.Count > 10) _codeHistory.RemoveAt(10);
            RefreshHistoryDropdown();
        }
    }

    private void RefreshHistoryDropdown()
    {
        _historyDropdown.Clear();
        _historyDropdown.AddItem("— 编码历史 —");
        foreach (var c in _codeHistory)
        {
            var label = c.Length > 40 ? c[..37] + "..." : c;
            _historyDropdown.AddItem(label);
        }
    }

    private void OnCompareCode()
    {
        FileLogger.Info("[NModSyncSubmenu] OnCompareCode");
        var code = _codeInput.Text.Trim().Replace("\n", "").Replace("\r", "");
        if (string.IsNullOrEmpty(code))
        {
            _codeResultLabel.Text = "请先粘贴编码";
            return;
        }
        try
        {
            var diffs = ModSyncCore.DecodeAndCompareV3(code);
            _currentDiffData = diffs;
            _hiddenOnLocal = ModSyncCore.ResolveHiddenMods(diffs, "local");
            FileLogger.Info($"[NModSyncSubmenu] OnCompareCode: {diffs.Count} diffs");
            _currentLocalMods = ModSyncCore.GetModsForSync();
            _codeResultLabel.Text = diffs.Count > 0 ? $"{diffs.Count}处差异" : "完全一致";
            RenderModTable();
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[NModSyncSubmenu] OnCompareCode error: {ex.Message}");
            _codeResultLabel.Text = $"对比失败: {ex.Message}";
        }
    }


    // ================================================================
    //  PROFILE COMPARISON (full: version/missing/extra/order)
    // ================================================================
    private List<ModDifference> CompareProfileFully(ModProfile profile, List<ModInfoSnapshot> local)
    {
        var diffs = new List<ModDifference>();
        var pdict = new Dictionary<string, (string v, int o, bool enabled)>();
        if (profile.Mods != null)
            foreach (var e in profile.Mods)
                pdict[e.Id] = (e.Version, e.LoadOrder, e.Enabled);

        foreach (var mod in local)
        {
            if (pdict.TryGetValue(mod.Id, out var pe))
            {
                if (mod.Version != pe.v && !string.IsNullOrEmpty(pe.v))
                    diffs.Add(new ModDifference { Id=mod.Id, Name=mod.Name, Type=DifferenceType.VersionMismatch, LocalVersion=mod.Version, RemoteVersion=pe.v, Details=$"本地:{mod.Version}→远程:{pe.v}" });
                else if (mod.IsEnabled != pe.enabled)
                    diffs.Add(new ModDifference { Id=mod.Id, Name=mod.Name, Type=DifferenceType.StateMismatch, LocalVersion=mod.IsEnabled?"Loaded":"Disabled", RemoteVersion=pe.enabled?"Enabled":"Disabled", Details=$"本地:{mod.State} → 配置:{(pe.enabled?"启用":"禁用")}" });
            }
            else
                diffs.Add(new ModDifference { Id=mod.Id, Name=mod.Name, Type=DifferenceType.ExtraMod, LocalVersion=mod.Version, IsToolMod=!mod.AffectsGameplay, Details=mod.AffectsGameplay?"本地额外（影响玩法）":"本地额外（工具MOD）" });
        }
        foreach (var kv in pdict)
            if (!local.Any(m => m.Id == kv.Key))
                diffs.Add(new ModDifference { Id=kv.Key, Name=kv.Key, Type=DifferenceType.MissingOnLocal, RemoteVersion=kv.Value.v, Details="本地缺失" });

        diffs.AddRange(ModSyncCore.CompareLoadOrder(local, profile));
        return diffs;
    }

    private void OnRefresh()
    {
        _currentDiffData = null;
        _currentLocalMods = null;
        _selectedProfilePath = null;
        _alphaSort = false;
        _alphaSortPending = false;
        _applyOrderBtn.Visible = false;
        _deleteProfileBtn.Visible = false;
        _profileSelector.Select(0);
        UpdateAlphaButtonState();
        RefreshData();
    }

    private void OnExport()
    {
        var result = ModSyncCore.ExportProfile("ModProfile");
        string shortName = Path.GetFileName(result);
        _statusLabel.Text = $"导出成功: {shortName}";
        // Show full path prominently (critical on platforms without file explorer)
        SetInfoMessage($"已导出到: {result}");
        RefreshProfileDropdown();
    }

    private void OnImport()
    {
        FileLogger.Info("[NModSyncSubmenu] OnImport");
        // v3.1.1: On mobile, FileDialog may not work — show paste dialog as fallback
        if (PlatformHelper.IsMobile)
        {
            ShowPasteImportDialog();
            return;
        }
        // Use file dialog (desktop)
        try
        {
            var fd = new FileDialog();
            fd.FileMode = FileDialog.FileModeEnum.OpenFile;
            fd.AddFilter("*.json", "JSON files");
            fd.FileSelected += (path) =>
            {
                ProcessImportFile(path);
            };
            AddChild(fd);
            fd.PopupCenteredRatio(0.6f);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[NModSyncSubmenu] FileDialog failed: {ex.Message} — using paste dialog");
            ShowPasteImportDialog();
        }
    }

    /// <summary>
    /// v3.1.1: Paste JSON text directly for platforms without FileDialog support.
    /// </summary>
    private void ShowPasteImportDialog()
    {
        var dialog = new AcceptDialog();
        dialog.Title = "粘贴 MOD 配置 JSON";
        dialog.Size = new Vector2I(550, 400);
        
        var vbox = new VBoxContainer();
        vbox.SetAnchorsPreset(LayoutPreset.FullRect);
        vbox.AddThemeConstantOverride("separation", 8);
        
        var label = new Label();
        label.Text = "请粘贴从队友处获取的 MOD 配置 JSON 内容：";
        label.AutowrapMode = TextServer.AutowrapMode.Word;
        label.AddThemeFontSizeOverride("font_size", S(14));
        vbox.AddChild(label);
        
        var textEdit = new TextEdit();
        textEdit.CustomMinimumSize = new Vector2(0, 200);
        textEdit.SizeFlagsVertical = SizeFlags.ExpandFill;
        textEdit.WrapMode = TextEdit.LineWrappingMode.Boundary;
        vbox.AddChild(textEdit);
        
        dialog.AddChild(vbox);
        AddChild(dialog);
        
        dialog.Confirmed += () =>
        {
            string json = textEdit.Text.Trim();
            if (string.IsNullOrEmpty(json))
            {
                _statusLabel.Text = "未粘贴内容";
                return;
            }
            // Save to temp file and process
            string tempPath = Path.Combine(ModSyncCore.ProfileDir, "_pasted_import.json");
            File.WriteAllText(tempPath, json);
            ProcessImportFile(tempPath);
        };
        
        dialog.PopupCenteredRatio(0.7f);
    }

    private void ProcessImportFile(string path)
    {
        var profile = ModSyncCore.ReadProfile(path);
        if (profile == null) { _statusLabel.Text = "导入失败: 无效的 JSON 格式"; return; }
        var localMods = ModSyncCore.GetModsForSync();
        var diffs = CompareProfileFully(profile, localMods);
        _statusLabel.Text = diffs.Count > 0 ? $"导入成功，{diffs.Count}处差异" : "导入成功，完全一致";
        _currentDiffData = diffs;
        _hiddenOnLocal = ModSyncCore.ResolveHiddenMods(diffs, "local");
        _currentLocalMods = localMods;
        RenderCompareTable();
        RefreshProfileDropdown();
    }

    private void OnCopyDiff()
    {
        _statusLabel.Text = "差异报告已复制到剪贴板";
        // Build diff text from current mods
        var lines = _localMods.Select(m =>
            $"[{m.LoadOrder:D2}] {m.Id} v{m.Version} ({m.State})");
        DisplayServer.ClipboardSet(string.Join("\n", lines));
    }

    private void OnApplyOrder()
    {
        // A-Z pending: confirm alphabetical sort AND write to settings.save
        if (_alphaSortPending)
        {
            _alphaSort = true;
            _alphaSortPending = false;
            UpdateAlphaButtonState();
            RefreshData();
            // Build alphabetical profile and write to settings.save
            var mods = ModSyncCore.GetModsForSync().OrderBy(m => m.Name).ToList();
            var azProfile = new ModProfile
            {
                ProfileName = "A-Z 字母排序",
                Mods = mods.Select(m => new ModProfileEntry
                {
                    Id = m.Id,
                    Version = m.Version,
                    Source = m.ModSource,
                    Enabled = m.IsEnabled
                }).ToList(),
                LoadOrder = mods.Select(m => $"{m.Id}::{m.ModSource}").ToList()
            };
            if (ModSyncCore.TryApplyProfileOrder(azProfile, out var writeErr))
                SetInfoMessage("已按字母排序写入（重启游戏生效）");
            else
                SetInfoMessage($"排序写入失败: {writeErr}");
            return;
        }
        if (_selectedProfilePath == null && _currentDiffData == null) return;
        // Check for blocking issues first
        if (_currentDiffData != null)
        {
            var hasBlocking = _currentDiffData.Any(d => d.Type != DifferenceType.OrderMismatch && d.Type != DifferenceType.ExtraMod);
            if (hasBlocking)
            {
                SetInfoMessage("请先处理关键差异（版本不一致/缺失等）再应用排序");
                return;
            }
        }
        if (_selectedProfilePath == null) return;
        var profile = ModSyncCore.ReadProfile(_selectedProfilePath);
        if (profile == null) return;
        ModSyncCore.TryApplyProfileOrder(profile, out var err);
        _statusLabel.Text = err ?? "排序已应用（重启后生效）";
        SetInfoMessage(err ?? "重启后生效");
    }

    // ================================================================
    //  A-Z SORT STATE MACHINE
    //  States: NORMAL → PENDING → APPLIED → NORMAL
    // ================================================================
    private void OnAlphaToggle()
    {
        if (_alphaSortPending)
        {
            // PENDING → NORMAL (cancel)
            _alphaSortPending = false;
        }
        else if (_alphaSort)
        {
            // APPLIED → NORMAL (toggle off)
            _alphaSort = false;
        }
        else
        {
            // NORMAL → PENDING (wait for confirm)
            _alphaSortPending = true;
        }
        UpdateAlphaButtonState();
    }

    private void UpdateAlphaButtonState()
    {
        // Determine button text
        string text;
        if (_alphaSortPending)
            text = "取消";
        else if (_alphaSort)
            text = "加载顺序";
        else
            text = "A-Z";

        if (_encAlphaBtn != null) _encAlphaBtn.Text = text;
        if (_impAlphaBtn != null) _impAlphaBtn.Text = text;

        // Force show "应用排序" button during PENDING state
        var pendingVisible = _alphaSortPending;
        // Import view
        if (_applyOrderBtn != null && _applyOrderBtn.Visible != pendingVisible)
        {
            // Only force-show in PENDING; otherwise keep original logic
            if (pendingVisible) _applyOrderBtn.Visible = true;
        }
        // Encoding view
        if (_encApplyOrderBtn != null)
            _encApplyOrderBtn.Visible = pendingVisible;
    }

    private void OnProfileSelected(long idx)
    {
        if (idx <= 0)
        {
            _selectedProfilePath = null;
            _deleteProfileBtn.Visible = false;
            _applyOrderBtn.Visible = _alphaSortPending; // keep visible during A-Z pending
            return;
        }
        int i = (int)idx - 1;
        if (i < _profilePaths.Count)
        {
            _selectedProfilePath = _profilePaths[i];
            _deleteProfileBtn.Visible = true;
            _applyOrderBtn.Visible = true;
            // Load and compare
            var profile = ModSyncCore.ReadProfile(_selectedProfilePath);
            if (profile != null)
            {
                var diffs = CompareProfileFully(profile, _localMods);
                _currentDiffData = diffs;
                _currentLocalMods = ModSyncCore.GetModsForSync();
                _statusLabel.Text = diffs.Count > 0 ? $"已加载，{diffs.Count}处差异" : "已加载，完全一致";
                RenderModTable();
            }
        }
    }

    private void OnDeleteProfile()
    {
        if (_selectedProfilePath == null) return;
        System.IO.File.Delete(_selectedProfilePath);
        _statusLabel.Text = "配置文件已删除";
        _selectedProfilePath = null;
        _deleteProfileBtn.Visible = false;
        _applyOrderBtn.Visible = _alphaSortPending;
        _profileSelector.Select(0);
        RefreshData();
    }

    // ================================================================
    //  HELPERS
    // ================================================================
    private static string GenerateSyncCode(List<ModInfoSnapshot> mods)
    {
        // v3.1.6: filter out hidden mods — same as ModSyncPanel.GenerateSyncCode
        List<string>? gameplayEntries = null;
        try
        {
            var modMgrType = Type.GetType("MegaCrit.Sts2.Core.Modding.ModManager, sts2");
            if (modMgrType != null)
            {
                var method = modMgrType.GetMethod("GetGameplayRelevantModNameList");
                gameplayEntries = method?.Invoke(null, null) as List<string>;
            }
        }
        catch { }

        var sorted = mods.OrderBy(m => m.LoadOrder).ToList();
        if (gameplayEntries != null && gameplayEntries.Count > 0)
        {
            var filtered = sorted.Where(m => gameplayEntries.Any(e => e.StartsWith(m.Id + "-"))).ToList();
            // v3.1.5-fix: if gameplay filter drops >30% of mods, it's unreliable (e.g. Android port).
            // Fall back to unfiltered list instead of silently dropping valid mods.
            if (filtered.Count > sorted.Count * 0.7)
                sorted = filtered;
            else
                GD.Print($"[ModSyncChecker] Gameplay filter unreliable: {filtered.Count}/{sorted.Count} mods. Using full list.");
        }
        var parts = new List<string> { $"v3|{sorted.Count}" };
        for (int i = 0; i < sorted.Count; i++)
        {
            var m = sorted[i];
            parts.Add($"{m.Id}~{m.Version}~{m.ModSource}~{(m.IsEnabled?1:0)}~{i}");
        }
        var data = string.Join("|", parts);
        var compressed = ModSyncCore.GzipCompress(data);
        var crc = ModSyncCore.ComputeCrc32(data);
        return $"#MSCv3#{compressed}#{crc:X8}";
    }

    private HBoxContainer MakeSection(string title, out VBoxContainer content)
    {
        var section = new PanelContainer();
        section.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        section.AddThemeStyleboxOverride("panel",
            new StyleBoxFlat
            {
                BgColor = C_SectionBg,
                CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
                ContentMarginLeft = 10, ContentMarginTop = 8,
                ContentMarginRight = 10, ContentMarginBottom = 8,
            });

        var inner = new VBoxContainer();
        inner.AddThemeConstantOverride("separation", 6);
        section.AddChild(inner);

        var header = new Label();
        header.Text = title;
        header.AddThemeColorOverride("font_color", C_Accent);
        header.AddThemeFontSizeOverride("font_size", S(13));
        inner.AddChild(header);

        inner.AddChild(MakeSep());

        content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 6);
        inner.AddChild(content);

        var wrapper = new HBoxContainer();
        wrapper.AddChild(section);
        return wrapper;
    }

    private static Button MakeBtn(string text, System.Action action)
    {
        var btn = new Button();
        btn.Text = text;
        btn.Pressed += () => action();
        return btn;
    }

    private static Button MakeSmallBtn(string text)
    {
        var btn = new Button();
        btn.Text = text;
        btn.CustomMinimumSize = new Vector2(32, 32);
        btn.Flat = true;
        return btn;
    }

    private Button MakeNavBtn(string text, bool active)
    {
        var btn = new Button();
        btn.Text = text;
        btn.Flat = true;
        btn.Alignment = HorizontalAlignment.Left;
        btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        btn.AddThemeFontSizeOverride("font_size", S(15));
        btn.AddThemeColorOverride("font_color", active ? C_Text : C_Dim);
        btn.AddThemeStyleboxOverride("normal", new StyleBoxFlat
        {
            BgColor = active ? new Color(0.12f, 0.12f, 0.18f) : new Color(0, 0, 0, 0),
            BorderWidthLeft = active ? 4 : 0,
            BorderColor = C_Accent,
            ContentMarginLeft = 14, ContentMarginTop = 11,
            ContentMarginRight = 14, ContentMarginBottom = 11,
        });
        return btn;
    }

    private static HSeparator MakeSep()
    {
        var sep = new HSeparator();
        sep.Modulate = new Color(0.12f, 0.12f, 0.18f);
        return sep;
    }

    private Control MakeColHandle()
    {
        var handle = new ColorRect();
        handle.Color = new Color(0.35f, 0.35f, 0.45f, 1f);
        // v3.1.1: configurable handle width (default 4px desktop, 12px touch)
        float hw = ModSyncCore.Config.HandleWidth;
        handle.CustomMinimumSize = new Vector2(hw, 0);
        handle.MouseDefaultCursorShape = CursorShape.Hsplit;
        handle.MouseFilter = MouseFilterEnum.Pass; // let parent catch events
        handle.GuiInput += OnColHandleGuiInput;
        handle.MouseExited += () => { if (!Input.IsMouseButtonPressed(MouseButton.Left)) _isDraggingHandle = false; };
        return handle;
    }

    private void OnColHandleGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                _isDraggingHandle = true;
                _handleDragStartX = mb.GlobalPosition.X;
                _handleDragStartColWidth = _nameColWidth;
                AcceptEvent();
            }
            else
            {
                _isDraggingHandle = false;
            }
        }
        // Motion is handled in _Input for free-range dragging
    }

    protected override void OnSubmenuShown()
    {
        base.OnSubmenuShown();
        RefreshData();
    }

    protected override void OnSubmenuHidden()
    {
        base.OnSubmenuHidden();
    }

    public override void _Input(InputEvent @event)
    {
        base._Input(@event);

        // Column handle drag — uses global input for free-range cursor movement
        if (_isDraggingHandle && @event is InputEventMouseMotion mm)
        {
            float delta = mm.GlobalPosition.X - _handleDragStartX;
            float maxW = GetViewportRect().Size.X - NavW - 200f;
            _nameColWidth = Mathf.Clamp(_handleDragStartColWidth + delta, 120f, maxW);
            if (_headerNameLbl != null)
                _headerNameLbl.CustomMinimumSize = new Vector2(_nameColWidth, 0);
            if (_encHeaderNameLbl != null)
                _encHeaderNameLbl.CustomMinimumSize = new Vector2(_nameColWidth, 0);
            RenderModTable();
            AcceptEvent();
        }
        if (!Input.IsMouseButtonPressed(MouseButton.Left))
        {
            if (_isDraggingHandle)
            {
                // Save column width on drag end
                ModSyncCore.UpdateColumnWidth(_nameColWidth);
            }
            _isDraggingHandle = false;
        }

    }

    // ===== v3.1.5: UI layout debug dump =====
    public override void _Process(double delta)
    {
        if (!ModSyncConfigNode.DebugUILayout) return;

        _debugFrameCounter++;
        if (_debugFrameCounter % 60 != 0) return; // every ~1s at 60fps

        string dump = $"[UI Debug] _encodingView Size=({_encodingView?.Size.X:F0},{_encodingView?.Size.Y:F0}) Pos=({_encodingView?.Position.X:F0},{_encodingView?.Position.Y:F0}) Visible={_encodingView?.Visible}"
            + $"\n[UI Debug] _codeResultLabel Size=({_codeResultLabel?.Size.X:F0},{_codeResultLabel?.Size.Y:F0}) Pos=({_codeResultLabel?.Position.X:F0},{_codeResultLabel?.Position.Y:F0}) Visible={_codeResultLabel?.Visible}";

        GD.Print(dump);
    }
}
