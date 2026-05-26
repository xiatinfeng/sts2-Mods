using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MapOddsTracker.Scripts.UI;

namespace MapOddsTracker.Scripts;

public enum NodeType
{
    Unknown,
    Monster,
    Elite,
    Boss,
    Shop,
    RestSite,
    Treasure,
    Ancient
}

public class MapNodeInfo
{
    public NodeType Type { get; set; }
    public string EncounterName { get; set; } = "";
    public int Row { get; set; }
    public bool IsVisited { get; set; }
}

public class EncounterQueueItem
{
    public int Index { get; set; }
    public string Name { get; set; } = "";   // 显示名称（中文或英文）
    public string Id { get; set; } = "";     // 英文 ID，用于图片映射（遭遇ID）
    public List<string> MonsterIds { get; set; } = new(); // 组合遭遇中的各怪物ID
    public NodeType Type { get; set; }
    public bool IsConsumed { get; set; }
}

public static class MapTracker
{
    private static readonly Dictionary<int, List<MapNodeInfo>> _actNodeData = new();
    private static readonly Dictionary<int, List<EncounterQueueItem>> _actNormalQueue = new();
    private static readonly Dictionary<int, List<EncounterQueueItem>> _actEliteQueue = new();
    private static readonly Dictionary<int, EncounterQueueItem> _actBossInfo = new();
    private static int _currentActIndex = 0;
    private static RunState? _capturedRunState;
    private static bool _initialized;
    private static MapOverlay? _overlay;

    public static int CurrentActNumber => _currentActIndex + 1;
    public static int TotalActs => _capturedRunState?.Acts.Count ?? 3;
    public static bool HasData => _initialized && _actNodeData.Count > 0;

    public static List<MapNodeInfo> GetActNodes(int actNumber)
    {
        EnsureGenerated();
        return _actNodeData.TryGetValue(actNumber, out var nodes) ? nodes : new List<MapNodeInfo>();
    }

    public static List<EncounterQueueItem> GetEncounterQueue(int actNumber, NodeType type)
    {
        EnsureGenerated();
        if (type == NodeType.Monster)
            return _actNormalQueue.TryGetValue(actNumber, out var q) ? q : new List<EncounterQueueItem>();
        if (type == NodeType.Elite)
            return _actEliteQueue.TryGetValue(actNumber, out var q) ? q : new List<EncounterQueueItem>();
        return new List<EncounterQueueItem>();
    }

    public static EncounterQueueItem? GetBossInfo(int actNumber)
    {
        EnsureGenerated();
        return _actBossInfo.TryGetValue(actNumber, out var boss) ? boss : null;
    }

    public static List<int> GetAllActNumbers()
    {
        EnsureGenerated();
        return _actNodeData.Keys.OrderBy(a => a).ToList();
    }

    private static NodeType FromMapPointType(MapPointType type)
    {
        return type switch
        {
            MapPointType.Monster => NodeType.Monster,
            MapPointType.Elite => NodeType.Elite,
            MapPointType.Boss => NodeType.Boss,
            MapPointType.Shop => NodeType.Shop,
            MapPointType.RestSite => NodeType.RestSite,
            MapPointType.Treasure => NodeType.Treasure,
            MapPointType.Ancient => NodeType.Ancient,
            MapPointType.Unknown => NodeType.Unknown,
            _ => NodeType.Unknown
        };
    }

    private static (string Name, string Id) GetEncounterInfo(EncounterModel? encounter)
    {
        if (encounter == null) return ("", "");
        string id = encounter.Id.Entry;
        try
        {
            var title = encounter.Title;
            if (title != null && !title.IsEmpty)
            {
                return (title.GetRawText(), id);
            }
        }
        catch (Exception ex)
        {
            ModLogger.LogErr($"Failed to get encounter name: {ex.Message}");
        }
        return (id, id);
    }

    /// <summary>
    /// Extract individual monster IDs from an encounter.
    /// For multi-monster encounters (e.g. gremlin gang), returns all distinct monster IDs.
    /// For single-monster or non-combat encounters, returns the encounter's own ID.
    /// </summary>
    private static List<string> GetEncounterMonsterIds(EncounterModel? encounter)
    {
        var result = new List<string>();
        if (encounter == null) return result;

        try
        {
            var allMonsters = encounter.AllPossibleMonsters;
            if (allMonsters != null)
            {
                foreach (var monster in allMonsters)
                {
                    if (monster?.Id?.Entry != null)
                    {
                        var monsterId = monster.Id.Entry;
                        if (!result.Contains(monsterId))
                            result.Add(monsterId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ModLogger.LogErr($"Failed to get AllPossibleMonsters for {encounter.Id.Entry}: {ex.Message}");
        }

        // Fallback: if no monsters extracted, use encounter ID itself
        if (result.Count == 0)
        {
            result.Add(encounter.Id.Entry);
        }

        return result;
    }

    public static void SetRunState(RunState state)
    {
        bool isSameState = _capturedRunState == state;
        if (!isSameState)
        {
            Reset();
            _capturedRunState = state;
            ModLogger.Log($"RunState captured. Seed: {state.Rng.StringSeed}");
        }

        // Always update current act index — it changes as player progresses through acts
        _currentActIndex = state.CurrentActIndex;

        if (!isSameState)
        {
            EnsureGenerated();
        }
        else
        {
            // Same RunState object, but player may have fought new encounters
            RefreshConsumptionState();
        }
    }

    private static void EnsureGenerated()
    {
        if (_initialized || _capturedRunState == null) return;
        var state = _capturedRunState;

        for (int actIdx = 0; actIdx < state.Acts.Count; actIdx++)
        {
            try
            {
                var act = state.Acts[actIdx];

                // === 优先使用实际游戏的地图 act.Map ===
                ActMap? map = null;
                try
                {
                    var mapProp = act.GetType().GetProperty(ModConstants.ActMapProperty, BindingFlags.Public | BindingFlags.Instance);
                    map = mapProp?.GetValue(act) as ActMap;
                }
                catch (Exception ex)
                {
                    ModLogger.LogErr($"Failed to get act.Map via reflection: {ex.Message}");
                }

                if (map == null)
                {
                    bool isMultiplayer = state.Players.Count > 1;
                    var mapRng = new Rng(state.Rng.Seed, string.Format(ModConstants.ActMapSeedFormat, actIdx + 1));
                    map = new StandardActMap(
                        mapRng,
                        act,
                        isMultiplayer,
                        shouldReplaceTreasureWithElites: false,
                        hasSecondBoss: act.HasSecondBoss
                    );
                    ModLogger.Log($"Act {actIdx + 1} map not available, using fallback RNG generation.");
                }

                // 读取 encounters
                List<EncounterModel> normalEncounters = new();
                List<EncounterModel> eliteEncounters = new();
                EncounterModel? bossEncounter = null;
                int normalEncountersVisited = 0;
                int eliteEncountersVisited = 0;

                try
                {
                    var roomsField = typeof(ActModel).GetField(ModConstants.RoomsField, BindingFlags.NonPublic | BindingFlags.Instance);
                    if (roomsField != null)
                    {
                        var roomSet = roomsField.GetValue(act);
                        if (roomSet != null)
                        {
                            var normalField = roomSet.GetType().GetField(ModConstants.NormalEncountersField, BindingFlags.Public | BindingFlags.Instance);
                            var eliteField = roomSet.GetType().GetField(ModConstants.EliteEncountersField, BindingFlags.Public | BindingFlags.Instance);
                            var bossProp = roomSet.GetType().GetProperty(ModConstants.BossProperty, BindingFlags.Public | BindingFlags.Instance);
                            var normalVisitedField = roomSet.GetType().GetField(ModConstants.NormalVisitedField, BindingFlags.Public | BindingFlags.Instance);
                            var eliteVisitedField = roomSet.GetType().GetField(ModConstants.EliteVisitedField, BindingFlags.Public | BindingFlags.Instance);

                            if (normalField != null)
                                normalEncounters = (List<EncounterModel>)normalField.GetValue(roomSet)! ?? new();
                            if (eliteField != null)
                                eliteEncounters = (List<EncounterModel>)eliteField.GetValue(roomSet)! ?? new();
                            if (bossProp != null)
                                bossEncounter = (EncounterModel?)bossProp.GetValue(roomSet);
                            if (normalVisitedField != null)
                                normalEncountersVisited = (int)(normalVisitedField.GetValue(roomSet) ?? 0);
                            if (eliteVisitedField != null)
                                eliteEncountersVisited = (int)(eliteVisitedField.GetValue(roomSet) ?? 0);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ModLogger.LogErr($"Failed to read rooms for act {actIdx + 1}: {ex.Message}");
                }

                // 获取已访问坐标
                HashSet<MapCoord> visitedCoords = new();
                try
                {
                    var visitedProp = state.GetType().GetProperty(ModConstants.VisitedCoordsProperty, BindingFlags.Public | BindingFlags.Instance);
                    var visitedList = visitedProp?.GetValue(state) as List<MapCoord>;
                    if (visitedList != null)
                    {
                        foreach (var coord in visitedList)
                            visitedCoords.Add(coord);
                    }
                }
                catch { }

                var allPoints = map.GetAllMapPoints().ToList();
                var sortedPoints = allPoints
                    .OrderBy(p => p.coord.row)
                    .ThenBy(p => p.coord.col)
                    .ToList();

                var nodes = new List<MapNodeInfo>();
                foreach (var point in sortedPoints)
                {
                    var nodeType = FromMapPointType(point.PointType);
                    bool isVisited = visitedCoords.Contains(point.coord);

                    nodes.Add(new MapNodeInfo
                    {
                        Type = nodeType,
                        EncounterName = "",
                        Row = point.coord.row,
                        IsVisited = isVisited
                    });
                }

                _actNodeData[actIdx + 1] = nodes;

                // 构建 Monster 消耗队列
                var normalQueue = new List<EncounterQueueItem>();
                for (int i = 0; i < normalEncounters.Count; i++)
                {
                    var info = GetEncounterInfo(normalEncounters[i]);
                    var monsterIds = GetEncounterMonsterIds(normalEncounters[i]);
                    normalQueue.Add(new EncounterQueueItem
                    {
                        Index = i + 1,
                        Name = info.Name,
                        Id = info.Id,
                        MonsterIds = monsterIds,
                        Type = NodeType.Monster,
                        IsConsumed = i < normalEncountersVisited
                    });
                }
                _actNormalQueue[actIdx + 1] = normalQueue;

                // 构建 Elite 消耗队列
                var eliteQueue = new List<EncounterQueueItem>();
                for (int i = 0; i < eliteEncounters.Count; i++)
                {
                    var info = GetEncounterInfo(eliteEncounters[i]);
                    var monsterIds = GetEncounterMonsterIds(eliteEncounters[i]);
                    eliteQueue.Add(new EncounterQueueItem
                    {
                        Index = i + 1,
                        Name = info.Name,
                        Id = info.Id,
                        MonsterIds = monsterIds,
                        Type = NodeType.Elite,
                        IsConsumed = i < eliteEncountersVisited
                    });
                }
                _actEliteQueue[actIdx + 1] = eliteQueue;

                // Boss 信息（每个ACT固定一个）
                if (bossEncounter != null)
                {
                    var bossInfo = GetEncounterInfo(bossEncounter);
                    var bossMonsterIds = GetEncounterMonsterIds(bossEncounter);
                    _actBossInfo[actIdx + 1] = new EncounterQueueItem
                    {
                        Index = 1,
                        Name = bossInfo.Name,
                        Id = bossInfo.Id,
                        MonsterIds = bossMonsterIds,
                        Type = NodeType.Boss,
                        IsConsumed = false
                    };
                }

                ModLogger.Log($"Act {actIdx + 1}: {nodes.Count} nodes, {normalQueue.Count} normal, {eliteQueue.Count} elite, boss={bossEncounter?.Id.Entry ?? "none"}, consumed {normalEncountersVisited}/{eliteEncountersVisited}");
            }
            catch (Exception ex)
            {
                ModLogger.LogErr($"Failed to generate map for act {actIdx + 1}: {ex.Message}");
            }
        }

        // Register boss IDs discovered from runtime data
        var allBossIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bossInfo in _actBossInfo.Values)
        {
            foreach (var monsterId in bossInfo.MonsterIds)
            {
                if (!string.IsNullOrWhiteSpace(monsterId))
                    allBossIds.Add(monsterId);
            }
        }
        if (allBossIds.Count > 0)
        {
            MonsterImageMapper.RegisterBossIds(allBossIds);
        }

        _initialized = true;
        ModLogger.Log($"Generated maps for {_actNodeData.Count} acts.");
    }

    /// <summary>
    /// Refresh only the IsConsumed flags on existing queues without regenerating everything.
    /// Called when the same RunState is updated (player fought new encounters or changed act).
    /// </summary>
    public static void RefreshConsumptionState()
    {
        if (_capturedRunState == null || !_initialized) return;

        var state = _capturedRunState;
        for (int actIdx = 0; actIdx < state.Acts.Count; actIdx++)
        {
            try
            {
                var act = state.Acts[actIdx];
                int normalEncountersVisited = 0;
                int eliteEncountersVisited = 0;

                // Read visited counters using the same reflection logic as EnsureGenerated
                var roomsField = typeof(ActModel).GetField(ModConstants.RoomsField, BindingFlags.NonPublic | BindingFlags.Instance);
                if (roomsField != null)
                {
                    var roomSet = roomsField.GetValue(act);
                    if (roomSet != null)
                    {
                        var normalVisitedField = roomSet.GetType().GetField(ModConstants.NormalVisitedField, BindingFlags.Public | BindingFlags.Instance);
                        var eliteVisitedField = roomSet.GetType().GetField(ModConstants.EliteVisitedField, BindingFlags.Public | BindingFlags.Instance);

                        if (normalVisitedField != null)
                            normalEncountersVisited = (int)(normalVisitedField.GetValue(roomSet) ?? 0);
                        if (eliteVisitedField != null)
                            eliteEncountersVisited = (int)(eliteVisitedField.GetValue(roomSet) ?? 0);
                    }
                }

                // Update normal queue consumption state
                if (_actNormalQueue.TryGetValue(actIdx + 1, out var normalQueue))
                {
                    for (int i = 0; i < normalQueue.Count; i++)
                        normalQueue[i].IsConsumed = i < normalEncountersVisited;
                }

                // Update elite queue consumption state
                if (_actEliteQueue.TryGetValue(actIdx + 1, out var eliteQueue))
                {
                    for (int i = 0; i < eliteQueue.Count; i++)
                        eliteQueue[i].IsConsumed = i < eliteEncountersVisited;
                }

                // Also update IsVisited on node data for consistency
                HashSet<MapCoord> visitedCoords = new();
                try
                {
                    var visitedProp = state.GetType().GetProperty(ModConstants.VisitedCoordsProperty, BindingFlags.Public | BindingFlags.Instance);
                    var visitedList = visitedProp?.GetValue(state) as List<MapCoord>;
                    if (visitedList != null)
                    {
                        foreach (var coord in visitedList)
                            visitedCoords.Add(coord);
                    }
                }
                catch { }

                if (_actNodeData.TryGetValue(actIdx + 1, out var nodes))
                {
                    foreach (var node in nodes)
                    {
                        // Note: we don't have MapCoord here, so we skip IsVisited refresh on nodes
                        // The important part (queue IsConsumed) is already updated above
                    }
                }

                ModLogger.Log($"Refreshed consumption for Act {actIdx + 1}: {normalEncountersVisited} normal, {eliteEncountersVisited} elite consumed.");
            }
            catch (Exception ex)
            {
                ModLogger.LogErr($"Failed to refresh consumption for act {actIdx + 1}: {ex.Message}");
            }
        }
    }

    public static void Reset()
    {
        _actNodeData.Clear();
        _actNormalQueue.Clear();
        _actEliteQueue.Clear();
        _actBossInfo.Clear();
        _capturedRunState = null;
        _initialized = false;
        _currentActIndex = 0;
    }

    public static void CreateOrRefreshOverlay()
    {
        try
        {
            var sceneTree = Engine.GetMainLoop() as SceneTree;
            var root = sceneTree?.Root;
            if (root == null) return;

            var existing = root.GetNodeOrNull(ModConstants.OverlayNodeName);
            if (existing != null)
            {
                if (existing is MapOverlay overlay && GodotObject.IsInstanceValid(overlay))
                {
                    overlay.OnGameActChanged(CurrentActNumber);
                }
                return;
            }

            _overlay = new MapOverlay();
            _overlay.Name = ModConstants.OverlayNodeName;
            root.AddChild(_overlay);
            ModLogger.Log("Overlay created!");
        }
        catch (Exception ex)
        {
            ModLogger.LogErr($"Failed to create overlay: {ex.Message}");
        }
    }

    #region Harmony Patches - 最小化：只保留NMapScreen.Open

    [HarmonyPatch(typeof(NMapScreen), "Open")]
    public static class NMapScreen_Open_Patch
    {
        public static void Postfix()
        {
            try
            {
                var sceneTree = Engine.GetMainLoop() as SceneTree;
                if (sceneTree == null) return;

                var timer = sceneTree.CreateTimer(0.1f);
                timer.Timeout += () =>
                {
                    try
                    {
                        var runMgrType = typeof(RunManager);
                        var instanceProp = runMgrType.GetProperty(ModConstants.RunManagerInstProperty, BindingFlags.Public | BindingFlags.Static);
                        var instance = instanceProp?.GetValue(null);
                        if (instance == null)
                        {
                            ModLogger.Log("RunManager.Instance is null.");
                            return;
                        }

                        var stateProp = runMgrType.GetProperty(ModConstants.RunManagerStateProperty, BindingFlags.NonPublic | BindingFlags.Instance);
                        var runState = stateProp?.GetValue(instance) as RunState;
                        if (runState == null)
                        {
                            ModLogger.Log("RunState not available yet, skipping.");
                            return;
                        }

                        SetRunState(runState);
                        EnsureGenerated();
                        CreateOrRefreshOverlay();
                    }
                    catch (Exception ex)
                    {
                        ModLogger.LogErr($"Deferred init failed: {ex.Message}");
                    }
                };
            }
            catch (Exception ex)
            {
                ModLogger.LogErr($"NMapScreen.Open postfix failed: {ex.Message}");
            }
        }
    }

    #endregion
}
