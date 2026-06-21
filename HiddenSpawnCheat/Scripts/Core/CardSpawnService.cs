using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using Sts2SpawnCheat.Patches;
namespace Sts2SpawnCheat.Core;

public static class CardSpawnService
{
    // ═══════════════════════════════════════════════════
    //  3+N 注入配置
    // ═══════════════════════════════════════════════════

    public static List<string> SelectedCardIdsForReward { get; set; } = new();
    public static int ExtraCardCount { get; set; } = 2;
    public static string? SelectedCharacterPoolId { get; set; }
    public static List<string> MarkedRelicIds { get; set; } = new();
    public static List<string> MarkedPotionIds { get; set; } = new();

    /// <summary>刷新按钮是否不消耗替换队列（默认 false=消耗）</summary>
    public static bool ReuseOnReroll { get; set; } = false;

    // ═══════════════════════════════════════════════════
    //  标记卡牌增强（升级/附魔）
    // ═══════════════════════════════════════════════════

    /// <summary>标记的卡牌替换时是否自动升级</summary>
    public static bool MarkedCardsUpgraded { get; set; } = false;

    /// <summary>标记的卡牌替换时应用的附魔 ID（null=不附魔）</summary>
    public static string? SelectedEnchantmentId { get; set; } = null;

    /// <summary>原生附魔中文翻译表</summary>
    public static readonly Dictionary<string, string> EnchantmentNames = new()
    {
        ["Sharp"] = "锋利", ["Nimble"] = "灵活", ["Vigorous"] = "活力",
        ["Instinct"] = "本能", ["Momentum"] = "动量", ["Imbued"] = "灌注",
        ["Goopy"] = "粘稠", ["Swift"] = "迅捷", ["Slither"] = "偏斜",
        ["Favored"] = "偏爱", ["RoyallyApproved"] = "王室认可", ["Corrupted"] = "腐化",
        ["Spiral"] = "螺旋", ["Steady"] = "稳定", ["Adroit"] = "灵巧",
        ["Clone"] = "克隆", ["Glam"] = "魅力", ["PerfectFit"] = "完美契合",
        ["Sown"] = "播种", ["SlumberingEssence"] = "沉睡精华", ["TezcatarasEmber"] = "余烬",
        ["SoulsPower"] = "灵魂之力",
    };

    /// <summary>获取附魔显示名（原生=中文，MOD=类名）</summary>
    public static string GetEnchantDisplayName(string id) =>
        EnchantmentNames.TryGetValue(id, out var cn) ? cn : id;


    /// <summary>已排序的附魔列表（持久化，用户可排序）</summary>
    public static List<string> EnchantmentOrder { get; set; } = new()
    {
        "Sharp", "Nimble", "Vigorous", "Instinct", "Momentum",
        "Imbued", "Goopy", "Swift", "Slither", "Favored",
        "RoyallyApproved", "Corrupted", "Spiral", "Steady", "Adroit"
    };

    private const string EnchantOrderFile = "spawn_cheat_enchant_order.json";

    public static void LoadEnchantOrder()
    {
        try
        {
            // 先扫描全局 EnchantmentModel 子类（发现 MOD 附魔）
            DiscoverModEnchantments();

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Godot", "app_userdata", "Slay the Spire 2", EnchantOrderFile);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
                if (loaded != null && loaded.Count > 0) EnchantmentOrder = loaded;
            }
        }
        catch { }
    }

    public static void SaveEnchantOrder()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Godot", "app_userdata", "Slay the Spire 2", EnchantOrderFile);
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(EnchantmentOrder);
            File.WriteAllText(path, json);
        }
        catch { }
    }

    public static void MoveEnchantUp(int idx)
    {
        if (idx <= 0 || idx >= EnchantmentOrder.Count) return;
        (EnchantmentOrder[idx], EnchantmentOrder[idx - 1]) = (EnchantmentOrder[idx - 1], EnchantmentOrder[idx]);
        SaveEnchantOrder();
    }

    public static void MoveEnchantDown(int idx)
    {
        if (idx < 0 || idx >= EnchantmentOrder.Count - 1) return;
        (EnchantmentOrder[idx], EnchantmentOrder[idx + 1]) = (EnchantmentOrder[idx + 1], EnchantmentOrder[idx]);
        SaveEnchantOrder();
    }

    /// <summary>扫描全局 EnchantmentModel 子类，发现 MOD 附魔</summary>
    public static void DiscoverModEnchantments()
    {
        try
        {
            var baseType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.EnchantmentModel");
            if (baseType == null) return;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.IsAbstract || t == baseType) continue;
                    if (baseType.IsAssignableFrom(t) && !EnchantmentOrder.Contains(t.Name))
                        EnchantmentOrder.Add(t.Name);
                }
            }
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════
    //  队列脏标记（供 UI 实时刷新）
    // ═══════════════════════════════════════════════════

    public static bool QueueDirty { get; set; } = false;

    // ═══════════════════════════════════════════════════
    //  收藏集系统 — 命名的队列快照
    // ═══════════════════════════════════════════════════

    public class CollectionSet
    {
        public string Name { get; set; } = "";
        public List<string> Cards { get; set; } = new();
        public List<string> Relics { get; set; } = new();
        public List<string> Potions { get; set; } = new();
    }

    private static string _collectionsPath = "";
    private static List<CollectionSet> _collections = new();

    public static IReadOnlyList<CollectionSet> Collections => _collections.AsReadOnly();

    public static void InitCollections()
    {
        try
        {
            _collectionsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Godot", "app_userdata", "Slay the Spire 2", "spawn_cheat_collections.json");
            if (File.Exists(_collectionsPath))
            {
                var json = File.ReadAllText(_collectionsPath);
                _collections = JsonSerializer.Deserialize<List<CollectionSet>>(json) ?? new();
            }
        }
        catch { _collections = new(); }
    }

    public static void SaveCollections()
    {
        try
        {
            var dir = Path.GetDirectoryName(_collectionsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_collections, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_collectionsPath, json);
        }
        catch { }
    }

    public static void SaveCurrentAsCollection(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _collections.Add(new CollectionSet
        {
            Name = name.Trim(),
            Cards = new List<string>(SelectedCardIdsForReward),
            Relics = new List<string>(MarkedRelicIds),
            Potions = new List<string>(MarkedPotionIds)
        });
        SaveCollections();
    }

    public static void AppendCollection(int index)
    {
        if (index < 0 || index >= _collections.Count) return;
        var set = _collections[index];
        foreach (var id in set.Cards)
            if (!SelectedCardIdsForReward.Contains(id)) SelectedCardIdsForReward.Add(id);
        foreach (var id in set.Relics)
            if (!MarkedRelicIds.Contains(id)) MarkedRelicIds.Add(id);
        foreach (var id in set.Potions)
            if (!MarkedPotionIds.Contains(id)) MarkedPotionIds.Add(id);
        RebuildReplacementQueue();
        RebuildRelicQueue();
        RebuildPotionQueue();
    }

    public static void RemoveCollection(int index)
    {
        if (index < 0 || index >= _collections.Count) return;
        _collections.RemoveAt(index);
        SaveCollections();
    }

    /// <summary>将当前标记追加到指定收藏集（覆盖更新其内容）</summary>
    public static void CurrentMarksToCollection(int index)
    {
        if (index < 0 || index >= _collections.Count) return;
        var set = _collections[index];
        set.Cards = new List<string>(SelectedCardIdsForReward);
        set.Relics = new List<string>(MarkedRelicIds);
        set.Potions = new List<string>(MarkedPotionIds);
        SaveCollections();
        // 清空当前标记
        SelectedCardIdsForReward.Clear();
        MarkedRelicIds.Clear();
        MarkedPotionIds.Clear();
        RebuildReplacementQueue();
        RebuildRelicQueue();
        RebuildPotionQueue();
    }

    // ═══════════════════════════════════════════════════
    //  遗物队列消耗式替换
    // ═══════════════════════════════════════════════════

    private static Queue<string> _relicReplacementQueue = new();

    /// <summary>从 MarkedRelicIds 重建替换队列</summary>
    public static void RebuildRelicQueue()
    {
        _relicReplacementQueue = new Queue<string>(MarkedRelicIds ?? new());
    }

    /// <summary>消费队列头的一个遗物，返回 null 表示队列空。同步清除标记。</summary>
    public static string? ConsumeNextRelic()
    {
        if (_relicReplacementQueue.Count == 0) return null;
        var id = _relicReplacementQueue.Dequeue();
        if (id != null) MarkedRelicIds.Remove(id);
        RebuildRelicQueue();
        QueueDirty = true;
        return id;
    }

    // ═══════════════════════════════════════════════════
    //  药水队列消耗式替换
    // ═══════════════════════════════════════════════════

    private static Queue<string> _potionReplacementQueue = new();

    /// <summary>从 MarkedPotionIds 重建替换队列</summary>
    public static void RebuildPotionQueue()
    {
        _potionReplacementQueue = new Queue<string>(MarkedPotionIds ?? new());
    }

    /// <summary>消费队列头的一个药水，返回 null 表示队列空。同步清除标记。</summary>
    public static string? ConsumeNextPotion()
    {
        if (_potionReplacementQueue.Count == 0) return null;
        var id = _potionReplacementQueue.Dequeue();
        if (id != null) MarkedPotionIds.Remove(id);
        RebuildPotionQueue();
        QueueDirty = true;
        return id;
    }

    // ═══════════════════════════════════════════════════
    //  队列消耗式顶替
    // ═══════════════════════════════════════════════════

    private static Queue<string> _replacementQueue = new();

    /// <summary>从 SelectedCardIdsForReward 重建替换队列</summary>
    public static void RebuildReplacementQueue()
    {
        _replacementQueue = new Queue<string>(SelectedCardIdsForReward ?? new());
        try { System.IO.File.AppendAllText(CardRewardPatch.DebugLogPath, $"[Diag] RebuildQueue: {_replacementQueue.Count} cards: [{string.Join(",", _replacementQueue)}]\n"); }
        catch { }
    }

    /// <summary>消费队列头的一张卡，返回 null 表示队列空。同步清除 SelectedCardIdsForReward 中的对应标记。</summary>
    public static string? ConsumeNextReplacement()
    {
        if (_replacementQueue.Count == 0) return null;
        var cardId = _replacementQueue.Dequeue();
        // 同步清除标记
        if (cardId != null) SelectedCardIdsForReward.Remove(cardId);
        // 重建队列反映最新状态
        RebuildReplacementQueue();
        QueueDirty = true;
        return cardId;
    }

    // ═══════════════════════════════════════════════════
    //  收藏系统
    // ═══════════════════════════════════════════════════

    public static List<string> FavoriteCardIds { get; set; } = new();
    public static List<string> FavoriteRelicIds { get; set; } = new();
    public static List<string> FavoritePotionIds { get; set; } = new();

    private static string? _savePath;
    private static string SavePath
    {
        get
        {
            if (_savePath != null) return _savePath;
            try
            {
                var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
                var dir = Path.Combine(appData, "Godot", "app_userdata", "Slay the Spire 2", "sts2-spawn-cheat");
                Directory.CreateDirectory(dir);
                _savePath = Path.Combine(dir, "favorites.json");
            }
            catch { _savePath = "favorites.json"; }
            return _savePath;
        }
    }

    public static void SaveFavorites()
    {
        try
        {
            var data = new
            {
                cardIds = FavoriteCardIds,
                relicIds = FavoriteRelicIds,
                potionIds = FavoritePotionIds
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(SavePath, json);
        }
        catch { }
    }

    public static void LoadFavorites()
    {
        try
        {
            if (!File.Exists(SavePath)) return;
            var json = File.ReadAllText(SavePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("cardIds", out var c)) FavoriteCardIds = c.Deserialize<List<string>>() ?? new();
            if (root.TryGetProperty("relicIds", out var r)) FavoriteRelicIds = r.Deserialize<List<string>>() ?? new();
            if (root.TryGetProperty("potionIds", out var p)) FavoritePotionIds = p.Deserialize<List<string>>() ?? new();
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════
    //  基础操作
    // ═══════════════════════════════════════════════════

    /// <summary>按 ID 查找卡牌原型</summary>
    public static CardModel? FindCard(string cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return null;
        return ModelDb.AllCards
            .FirstOrDefault(c => ((AbstractModel)c).Id.Entry == cardId);
    }

    /// <summary>直接加卡入牌组，不经过 CardReward._cards，不会记录到 CardChoices</summary>
    public static void AddCardToDeck(string cardId)
    {
        var cardModel = FindCard(cardId);
        if (cardModel == null) return;

        try
        {
            var runManager = AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager")
                ?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (runManager == null) return;

            var state = runManager.GetType().GetMethod("DebugOnlyGetState",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
                ?.Invoke(runManager, null);
            if (state == null) return;

            var tCardModel = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.CardModel");
            var tPlayer = AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.Players.Player");
            if (tCardModel == null || tPlayer == null) return;

            var createCard = state.GetType().GetMethod("CreateCard",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { tCardModel, tPlayer }, null);
            if (createCard == null) return;

            var playerProp = tPlayer?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
            var player = playerProp?.GetValue(null);
            if (player == null) return;

            var card = createCard.Invoke(state, new[] { cardModel, player });
            if (card == null) return;

            var tCardPileCmd = AccessTools.TypeByName("MegaCrit.Sts2.Core.Commands.CardPileCmd");
            var mAdd = tCardPileCmd?.GetMethod("Add", new[] { tCardModel, AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.PileType") });
            if (mAdd == null) return;

            var tTaskHelper = AccessTools.TypeByName("MegaCrit.Sts2.Core.Helpers.TaskHelper");
            var mRunSafely = tTaskHelper?.GetMethod("RunSafely", new[] { typeof(System.Threading.Tasks.Task) });
            if (mRunSafely == null) return;

            var pileTypeDeck = System.Enum.Parse(AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.PileType"), "Deck");
            var addTask = mAdd.Invoke(null, new[] { card, pileTypeDeck }) as System.Threading.Tasks.Task;
            if (addTask != null) mRunSafely.Invoke(null, new[] { addTask });
        }
        catch { }
    }
}