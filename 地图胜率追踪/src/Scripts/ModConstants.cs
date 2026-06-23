namespace MapOddsTracker.Scripts;

/// <summary>
/// Centralized constants for MapOddsTracker mod.
/// Eliminates hardcoded string literals across the codebase.
/// </summary>
public static class ModConstants
{
    // ========================================================================
    // Logging
    // ========================================================================
    public const string LogPrefix = "[MapOddsTracker]";

    // ========================================================================
    // Mod Identity
    // ========================================================================
    public const string HarmonyId = "sts2.user.mapoddstracker";
    public const string ModInitMethod = "Init";

    // ========================================================================
    // Scene / Node Names
    // ========================================================================
    public const string OverlayNodeName = "MapOddsOverlay";

    // ========================================================================
    // Reflection: ActModel members
    // ========================================================================
    public const string ActMapProperty = "Map";

    // ========================================================================
    // Reflection: RoomSet (ActModel._rooms) members
    // ========================================================================
    public const string RoomsField = "_rooms";
    public const string NormalEncountersField = "normalEncounters";
    public const string EliteEncountersField = "eliteEncounters";
    public const string BossProperty = "Boss";
    public const string NormalVisitedField = "normalEncountersVisited";
    public const string EliteVisitedField = "eliteEncountersVisited";

    // ========================================================================
    // Reflection: RunState members
    // ========================================================================
    public const string VisitedCoordsProperty = "VisitedMapCoords";

    // ========================================================================
    // Reflection: RunManager members
    // ========================================================================
    public const string RunManagerInstProperty = "Instance";
    public const string RunManagerStateProperty = "State";

    // ========================================================================
    // RNG Seeds
    // ========================================================================
    public const string ActMapSeedFormat = "act_{0}_map";

    // ========================================================================
    // Asset Directories
    // ========================================================================
    public const string AssetsDir = "assets";
    public const string BossesDir = "bosses";
    public const string MonstersDir = "monsters";
    public const string ManualDir = "manual";

    // ========================================================================
    // Image Extensions
    // ========================================================================
    public const string PngExt = ".png";
    public const string WebpExt = ".webp";

    // ========================================================================
    // Boss Image Suffix
    // ========================================================================
    public const string BossImageSuffix = "_boss";

    // ========================================================================
    // UI Labels (MapOverlay)
    // ========================================================================
    public const string UiTitle = "遭遇队列 (按 J 切换)";
    public const string UiDragHandle = "☰";
    public const string UiDragTooltip = "拖拽移动";
    public const string UiZoomIcon = "🔍";
    public const string UiFilterAll = "全部";
    public const string UiFilterNormal = "⚔ 普通";
    public const string UiFilterElite = "◆ 精英";
    public const string UiActFormat = "ACT{0}";
    public const string UiEmptyHint = "(进入地图后显示)";
    public const string UiNoData = "(无数据 - 请开始一局新游戏)";
    public const string UiLegend = "灰色=已遭遇 | 白色=未遭遇";
    public const string UiMonsterTitle = "⚔ 普通怪物";
    public const string UiEliteTitle = "◆ 精英怪物";
    public const string UiBossTitle = "👑 Boss";
    public const string UiNoImage = "(无图片)";

    // ========================================================================
    // File Extensions (asset/scan helpers)
    // ========================================================================
    public static readonly string[] ImageExtensions = { PngExt, WebpExt };
}
