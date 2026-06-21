using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using Sts2SpawnCheat.Patches;

namespace Sts2SpawnCheat.Core;

/// <summary>
/// 商店作弊核心服务。
/// 
/// 完全参照 Jianbao233-RefreshShop v0.1.2（反编译验证）：
/// 1. NMerchantNode.Inventory → Player
/// 2. CreateForNormalMerchant(player)
/// 3. 替换 NMerchantRoom.Instance.Room.<Inventory>k__BackingField
/// 4. UnsubscribeOldEntries + DisconnectOldSlotSignals
/// 5. 把 NMerchantNode.<Inventory>k__BackingField 设 null（绕过 Initialize 的 "already populated" 守卫）
/// 6. 调 Initialize(newInventory, dialogue)
/// 7. UpdateNavigation
/// </summary>
public static class ShopSpawnService
{
    private static readonly Dictionary<string, Type?> _typeCache = new();

    /// <summary>刷新当前商店库存并刷新 UI。传入 NMerchantInventory 实例。</summary>
    public static bool RefreshShopOnNode(object inventoryNode)
    {
        if (inventoryNode == null) { GD.PrintErr("[SpawnCheat] RefreshShop: inventoryNode null"); return false; }
        if (!RunManager.Instance.IsInProgress) { GD.PrintErr("[SpawnCheat] RefreshShop: not in a run"); return false; }

        try
        {
            var nodeType = inventoryNode.GetType();

            // ── 1. NMerchantNode.Inventory → Player ──
            var invProp = nodeType.GetProperty("Inventory", BindingFlags.Public | BindingFlags.Instance);
            var oldInventory = invProp?.GetValue(inventoryNode);
            if (oldInventory == null) { GD.PrintErr("[SpawnCheat] RefreshShop: Inventory on node is null"); return false; }

            var playerProp = oldInventory.GetType().GetProperty("Player", BindingFlags.Public | BindingFlags.Instance);
            var player = playerProp?.GetValue(oldInventory);
            if (player == null) { GD.PrintErr("[SpawnCheat] RefreshShop: player null"); return false; }

            // ── 2. 创建新 MerchantInventory（手动构建，不消耗遗物池）──
            // 不走 CreateForNormalMerchant（它会调用 PopulateRelicEntries → 消耗 RelicGrabBag）
            // 改为：创建空库存 → 手动 Populate 卡牌/药水 → 从 AllRelics 随机选遗物（不碰池）
            var merchantInvType = FindTypeCached("MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory");
            var playerType = FindTypeCached("MegaCrit.Sts2.Core.Entities.Players.Player");
            if (merchantInvType == null || playerType == null)
            { GD.PrintErr("[SpawnCheat] RefreshShop: type lookup failed"); return false; }

            var newInventory = BuildShopInventory(merchantInvType, playerType, player);
            if (newInventory == null) { GD.PrintErr("[SpawnCheat] RefreshShop: BuildShopInventory failed"); return false; }

            // ── 3. 替换 NMerchantRoom.Instance.Room.<Inventory>k__BackingField ──
            var nMerchantRoomType = FindTypeCached("MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom");
            var nMerchantRoom = nMerchantRoomType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (nMerchantRoom != null)
            {
                var merchantRoom = nMerchantRoom.GetType().GetProperty("Room")?.GetValue(nMerchantRoom);
                merchantRoom?.GetType().GetField("<Inventory>k__BackingField",
                    BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(merchantRoom, newInventory);
            }

            // ── 4. 取消旧事件 + 断开旧信号 ──
            UnsubscribeOldEntries(inventoryNode, oldInventory);
            DisconnectOldSlotSignals(inventoryNode);

            // ── 5. 把 NMerchantNode 内部 Inventory 字段设 null（绕过 populated 守卫） ──
            nodeType.GetField("<Inventory>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(inventoryNode, null);

            // ── 6. 获取 dialogue ──
            object? dialogue = nMerchantRoom?.GetType().GetField("_dialogue",
                BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(nMerchantRoom);

            // ── 7. 调 Initialize（Postfix 会重新注入刷新按钮）──
            nodeType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(inventoryNode, new[] { newInventory, dialogue });

            // ── 8. UpdateNavigation ──
            nodeType.GetMethod("UpdateNavigation", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(inventoryNode, null);

            GD.Print("[SpawnCheat] Shop refreshed successfully");
            return true;
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException != null ? $" | Inner: {ex.InnerException.GetType()}: {ex.InnerException.Message}" : "";
            GD.PrintErr($"[SpawnCheat] RefreshShop failed: {ex.GetType()}: {ex.Message}{inner}");
            return false;
        }
    }

    // ── 4a. 取消旧 PurchaseCompleted/PurchaseFailed 事件 ──
    private static void UnsubscribeOldEntries(object invNode, object oldInventory)
    {
        try
        {
            var allEntries = oldInventory.GetType().GetProperty("AllEntries")?.GetValue(oldInventory) as IEnumerable;
            if (allEntries == null) return;

            var onPurchaseCompleted = invNode.GetType().GetMethod("OnPurchaseCompleted",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var merchantDialogue = invNode.GetType().GetField("_merchantDialogue",
                BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(invNode);
            var showForPurchaseAttempt = merchantDialogue?.GetType().GetMethod("ShowForPurchaseAttempt",
                BindingFlags.Public | BindingFlags.Instance);

            foreach (var entry in allEntries)
            {
                if (entry == null) continue;
                var entryType = entry.GetType();

                // PurchaseCompleted → invNode.OnPurchaseCompleted
                var pcEvent = FindEventRecursive(entryType, "PurchaseCompleted");
                if (pcEvent != null && onPurchaseCompleted != null)
                {
                    var del = Delegate.CreateDelegate(pcEvent.EventHandlerType, invNode, onPurchaseCompleted, throwOnBindFailure: false);
                    if (del != null) pcEvent.RemoveEventHandler(entry, del);
                }

                // PurchaseFailed → merchantDialogue.ShowForPurchaseAttempt
                var pfEvent = FindEventRecursive(entryType, "PurchaseFailed");
                if (pfEvent != null && showForPurchaseAttempt != null && merchantDialogue != null)
                {
                    var del = Delegate.CreateDelegate(pfEvent.EventHandlerType, merchantDialogue, showForPurchaseAttempt, throwOnBindFailure: false);
                    if (del != null) pfEvent.RemoveEventHandler(entry, del);
                }
            }

            GD.Print("[SpawnCheat] Unsubscribed old entries");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpawnCheat] UnsubscribeOldEntries failed (non-fatal): {ex.Message}");
        }
    }

    // ── 4b. 断开旧 slot 的 focus_entered 信号 ──
    private static void DisconnectOldSlotSignals(object invNode)
    {
        try
        {
            var getAllSlots = invNode.GetType().GetMethod("GetAllSlots",
                BindingFlags.Public | BindingFlags.Instance);
            var slots = getAllSlots?.Invoke(invNode, null) as IEnumerable;
            if (slots == null) return;

            foreach (var slot in slots)
            {
                if (slot is not Node slotNode) continue;
                try
                {
                    var connections = ((GodotObject)slotNode).GetSignalConnectionList(new StringName("focus_entered"));
                    foreach (var conn in connections)
                    {
                        if (conn.TryGetValue(new StringName("callable"), out var variant) && variant.VariantType == Variant.Type.Callable)
                        {
                            var callable = variant.AsCallable();
                            ((GodotObject)slotNode).Disconnect(new StringName("focus_entered"), callable);
                        }
                    }
                }
                catch { }
            }

            GD.Print("[SpawnCheat] Disconnected old slot signals");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpawnCheat] DisconnectOldSlotSignals failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>手动构建 MerchantInventory（不调用 PopulateRelicEntries，不消耗 RelicGrabBag）</summary>
    private static object? BuildShopInventory(Type merchantInvType, Type playerType, object player)
    {
        try
        {
            // 1. new MerchantInventory(player)
            var ctor = merchantInvType.GetConstructor(new[] { playerType });
            if (ctor == null) { GD.PrintErr("[SpawnCheat] BuildShop: MerchantInventory ctor not found"); return null; }
            var inventory = ctor.Invoke(new[] { player });

            // 2. PopulateCharacterCardEntries() — 安全，不消耗池
            var method = merchantInvType.GetMethod("PopulateCharacterCardEntries",
                BindingFlags.NonPublic | BindingFlags.Instance);
            method?.Invoke(inventory, null);

            // 3. PopulateColorlessCardEntries() — 安全
            method = merchantInvType.GetMethod("PopulateColorlessCardEntries",
                BindingFlags.NonPublic | BindingFlags.Instance);
            method?.Invoke(inventory, null);

            // 4. PopulatePotionEntries() — 安全
            method = merchantInvType.GetMethod("PopulatePotionEntries",
                BindingFlags.NonPublic | BindingFlags.Instance);
            method?.Invoke(inventory, null);

            // 5. CardRemovalEntry = new MerchantCardRemovalEntry(player)
            var removalType = FindTypeCached("MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardRemovalEntry");
            var removalCtor = removalType?.GetConstructor(new[] { playerType });
            if (removalCtor != null)
            {
                var removalEntry = removalCtor.Invoke(new[] { player });
                var removalField = merchantInvType.GetField("CardRemovalEntry",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                removalField?.SetValue(inventory, removalEntry);
            }

            // 6. 手动创建遗物条目（标记优先 + 随机补齐，不消耗 RelicGrabBag）
            BuildRelicEntries(merchantInvType, inventory, player, playerType);

            // 6b. 药水条目：Populate 后标记的药水替换前几个位置
            InjectMarkedPotions(merchantInvType, inventory, player, playerType);

            // 6c. 卡牌条目：标记的卡牌替换商店卡牌
            InjectMarkedCards(merchantInvType, inventory, player);

            // 6d. 注入商店后同步消耗所有队列（不消耗模式 ON 时跳过，留给战斗奖励）
            if (!CardSpawnService.ReuseOnReroll)
            {
                while (CardSpawnService.MarkedRelicIds.Count > 0)
                {
                    var r = CardSpawnService.ConsumeNextRelic();
                    if (r != null) CardRewardPatch.DiagLog($"Shop: consumed relic [{r}]");
                }
                while (CardSpawnService.MarkedPotionIds.Count > 0)
                {
                    var p = CardSpawnService.ConsumeNextPotion();
                    if (p != null) CardRewardPatch.DiagLog($"Shop: consumed potion [{p}]");
                }
            }

            // 7. 订阅 PurchaseCompleted 事件
            var allEntriesProp = merchantInvType.GetProperty("AllEntries",
                BindingFlags.Public | BindingFlags.Instance);
            var allEntries = allEntriesProp?.GetValue(inventory) as System.Collections.IEnumerable;
            var updateMethod = merchantInvType.GetMethod("UpdateEntries",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (allEntries != null && updateMethod != null)
            {
                foreach (var entry in allEntries)
                {
                    if (entry == null) continue;
                    var evt = FindEventRecursive(entry.GetType(), "PurchaseCompleted");
                    if (evt != null)
                    {
                        var del = Delegate.CreateDelegate(evt.EventHandlerType, inventory, updateMethod, throwOnBindFailure: false);
                        if (del != null) evt.AddEventHandler(entry, del);
                    }
                }
            }

            GD.Print("[SpawnCheat] BuildShop: inventory built without consuming relic bag");
            return inventory;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpawnCheat] BuildShop failed: {ex.GetType()}: {ex.Message}");
            return null;
        }
    }

    /// <summary>从 ModelDb.AllRelics 获取干净的遗物列表（排除弃用/保底/事件池）</summary>
    private static List<object> GetCleanRelicList()
    {
        var result = new List<object>();
        try
        {
            var modelDbType = FindTypeCached("MegaCrit.Sts2.Core.Models.ModelDb");
            if (modelDbType == null) return result;

            var allRelics = modelDbType.GetProperty("AllRelics", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as System.Collections.IEnumerable;
            if (allRelics == null) return result;

            // 排除池
            var excludedNames = new HashSet<string> { "DeprecatedRelicPool", "FallbackRelicPool", "EventRelicPool" };
            var excludedIds = new HashSet<string>();
            var allPools = modelDbType.GetProperty("AllRelicPools", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as System.Collections.IEnumerable;
            if (allPools != null)
            {
                foreach (var pool in allPools)
                {
                    if (pool == null || !excludedNames.Contains(pool.GetType().Name)) continue;
                    var idsProp = pool.GetType().GetProperty("AllRelicIds", BindingFlags.Public | BindingFlags.Instance);
                    var ids = idsProp?.GetValue(pool) as System.Collections.IEnumerable;
                    if (ids == null) continue;
                    foreach (var id in ids)
                        if (id != null) excludedIds.Add(id.ToString() ?? "");
                }
            }

            foreach (var relic in allRelics)
            {
                if (relic == null) continue;
                var idObj = relic.GetType().GetProperty("Id")?.GetValue(relic);
                var idStr = idObj?.GetType().GetProperty("Entry")?.GetValue(idObj) as string;
                if (idStr != null && excludedIds.Contains(idStr)) continue;
                result.Add(relic);
            }
        }
        catch { }
        return result;
    }

    /// <summary>获取 RelicRarity 枚举值</summary>
    private static object? GetRelicRarity(string name)
    {
        var rarityType = FindTypeCached("MegaCrit.Sts2.Core.Entities.Relics.RelicRarity");
        if (rarityType == null) return null;
        foreach (var val in Enum.GetValues(rarityType))
        {
            if (val.ToString() == name) return val;
        }
        return null;
    }

    /// <summary>用 50%/33%/17% 概率 Roll Common/Uncommon/Rare</summary>
    private static object? RollRandomRarity()
    {
        var roll = new Random().NextDouble();
        var name = roll < 0.50 ? "Common" : (roll < 0.83 ? "Uncommon" : "Rare");
        return GetRelicRarity(name);
    }

    private static EventInfo? FindEventRecursive(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var evt = t.GetEvent(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (evt != null) return evt;
        }
        return null;
    }

    /// <summary>无参数版本（面板按钮用，通过场景树找 NMerchantInventory 节点）</summary>
    public static bool RefreshShop()
    {
        var sceneTree = Engine.GetMainLoop() as SceneTree;
        var node = FindMerchantInventoryNode(sceneTree?.Root);
        if (node == null) { GD.PrintErr("[SpawnCheat] RefreshShop: NMerchantInventory node not found in scene tree"); return false; }
        return RefreshShopOnNode(node);
    }

    private static Node? FindMerchantInventoryNode(Node? parent)
    {
        if (parent == null) return null;
        if (parent.GetType().Name == "NMerchantInventory") return parent;
        foreach (var child in parent.GetChildren())
        {
            var found = FindMerchantInventoryNode(child);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>构建商店遗物条目：标记优先，随机补齐到 3 个</summary>
    private static void BuildRelicEntries(Type merchantInvType, object inventory, object player, Type playerType)
    {
        try
        {
            var relicEntryType = FindTypeCached("MegaCrit.Sts2.Core.Entities.Merchant.MerchantRelicEntry");
            var relicModelType = FindTypeCached("MegaCrit.Sts2.Core.Models.RelicModel");
            var relicCtor = relicEntryType?.GetConstructor(new[] { relicModelType, playerType });
            var toMutable = relicModelType?.GetMethod("ToMutable", BindingFlags.Public | BindingFlags.Instance);
            if (relicCtor == null || toMutable == null) return;

            var relicList = merchantInvType.GetField("_relicEntries",
                BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(inventory) as System.Collections.IList;
            if (relicList == null) return;
            relicList.Clear();

            // 标记的遗物优先
            var markedRelics = new List<object>();
            var modelDbType = FindTypeCached("MegaCrit.Sts2.Core.Models.ModelDb");
            var allRelics = modelDbType?.GetProperty("AllRelics", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as System.Collections.IEnumerable;
            if (allRelics != null && CardSpawnService.MarkedRelicIds.Count > 0)
            {
                foreach (var relic in allRelics)
                {
                    if (relic == null) continue;
                    var idObj = relic.GetType().GetProperty("Id")?.GetValue(relic);
                    var idStr = idObj?.GetType().GetProperty("Entry")?.GetValue(idObj) as string;
                    if (idStr != null && CardSpawnService.MarkedRelicIds.Contains(idStr))
                        markedRelics.Add(relic);
                }
            }

            // 随机补齐到 3 个
            var cleanRelics = GetCleanRelicList();
            var byRarity = new Dictionary<object?, List<object>>();
            foreach (var relic in cleanRelics)
            {
                var r = relic.GetType().GetProperty("Rarity")?.GetValue(relic);
                if (r == null) continue;
                if (!byRarity.ContainsKey(r)) byRarity[r] = new List<object>();
                byRarity[r].Add(relic);
            }

            var rng = new Random();
            var rarities = new[] { GetRelicRarity("Shop"), RollRandomRarity(), RollRandomRarity() };

            // 先加标记的
            foreach (var relic in markedRelics)
            {
                var mutable = toMutable.Invoke(relic, null);
                if (mutable != null)
                {
                    var entry = relicCtor.Invoke(new[] { mutable, player });
                    relicList.Add(entry);
                }
            }

            // 随机补齐到 3 个
            for (int i = relicList.Count; i < 3 && i < rarities.Length; i++)
            {
                var rarity = rarities[i];
                if (rarity == null || !byRarity.TryGetValue(rarity, out var candidates) || candidates.Count == 0)
                {
                    rarity = GetRelicRarity("Shop");
                    if (rarity == null || !byRarity.TryGetValue(rarity, out candidates)) continue;
                }
                var pick = candidates[rng.Next(candidates.Count)];
                var mutable = toMutable.Invoke(pick, null);
                if (mutable != null)
                {
                    var entry = relicCtor.Invoke(new[] { mutable, player });
                    relicList.Add(entry);
                }
            }

            GD.Print($"[SpawnCheat] Shop relics: {markedRelics.Count} marked + {relicList.Count - markedRelics.Count} random");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpawnCheat] BuildRelicEntries failed: {ex.Message}");
        }
    }

    /// <summary>在商店药水中注入标记的药水</summary>
    private static void InjectMarkedPotions(Type merchantInvType, object inventory, object player, Type playerType)
    {
        try
        {
            if (CardSpawnService.MarkedPotionIds.Count == 0) return;

            var potionModelType = FindTypeCached("MegaCrit.Sts2.Core.Models.PotionModel");
            var potionEntryType = FindTypeCached("MegaCrit.Sts2.Core.Entities.Merchant.MerchantPotionEntry");
            if (potionModelType == null || potionEntryType == null) return;

            // 找标记的药水对应的 PotionModel
            var modelDbType = FindTypeCached("MegaCrit.Sts2.Core.Models.ModelDb");
            var allPotions = modelDbType?.GetProperty("AllPotions", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as System.Collections.IEnumerable;
            if (allPotions == null) return;

            var markedModels = new List<object>();
            foreach (var potion in allPotions)
            {
                if (potion == null) continue;
                var idObj = potion.GetType().GetProperty("Id")?.GetValue(potion);
                var idStr = idObj?.GetType().GetProperty("Entry")?.GetValue(idObj) as string;
                if (idStr != null && CardSpawnService.MarkedPotionIds.Contains(idStr))
                    markedModels.Add(potion);
            }

            if (markedModels.Count == 0) return;

            // 取 _potionEntries 列表，替换前几个
            var potionList = merchantInvType.GetField("_potionEntries",
                BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(inventory) as System.Collections.IList;
            if (potionList == null) return;

            var toMutable = potionModelType.GetMethod("ToMutable", BindingFlags.Public | BindingFlags.Instance);

            for (int i = 0; i < markedModels.Count && i < potionList.Count; i++)
            {
                var mutable = toMutable?.Invoke(markedModels[i], null);
                if (mutable == null) continue;

                // MerchantPotionEntry(MutablePotion, Player) 或 (PotionModel, Player)
                var ctor = potionEntryType.GetConstructor(new[] { potionModelType, playerType });
                if (ctor == null)
                {
                    // 尝试 (MutablePotion, Player)
                    var mutablePotionType = FindTypeCached("MegaCrit.Sts2.Core.Entities.Potions.MutablePotion");
                    if (mutablePotionType != null)
                        ctor = potionEntryType.GetConstructor(new[] { mutablePotionType, playerType });
                }
                if (ctor == null) break;

                var entry = ctor.Invoke(new[] { mutable, player });
                potionList[i] = entry;
            }

            GD.Print($"[SpawnCheat] Shop potions: injected {markedModels.Count} marked");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpawnCheat] InjectMarkedPotions failed: {ex.Message}");
        }
    }

    /// <summary>在商店卡牌中注入标记的卡牌</summary>
    private static void InjectMarkedCards(Type merchantInvType, object inventory, object player)
    {
        try
        {
            if (CardSpawnService.SelectedCardIdsForReward.Count == 0) return;

            // 获取 state (RunManager.DebugOnlyGetState())
            var runManager = AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager")
                ?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (runManager == null) return;
            var getState = runManager.GetType().GetMethod("DebugOnlyGetState",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            var state = getState?.Invoke(runManager, null);
            if (state == null) return;

            var cardModelType = FindTypeCached("MegaCrit.Sts2.Core.Models.CardModel");
            var playerType = FindTypeCached("MegaCrit.Sts2.Core.Entities.Players.Player");
            var creationResultType = FindTypeCached("MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult");
            if (cardModelType == null || playerType == null || creationResultType == null) return;

            // state.CreateCard(CardModel, Player)
            var createCard = state.GetType().GetMethod("CreateCard",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { cardModelType, playerType }, null);
            if (createCard == null) return;

            // 合并角色卡 + 无色卡列表
            var charField = merchantInvType.GetField("_characterCardEntries",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var colorField = merchantInvType.GetField("_colorlessCardEntries",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var allCardEntries = new System.Collections.ArrayList();
            if (charField?.GetValue(inventory) is System.Collections.IList cl)
                foreach (var e in cl) allCardEntries.Add(e);
            if (colorField?.GetValue(inventory) is System.Collections.IList col)
                foreach (var e in col) allCardEntries.Add(e);

            if (allCardEntries.Count == 0) return;

            int injected = 0;
            foreach (var cardId in CardSpawnService.SelectedCardIdsForReward)
            {
                if (injected >= allCardEntries.Count) break;

                var cardModel = CardSpawnService.FindCard(cardId);
                if (cardModel == null) continue;

                var createdCard = createCard.Invoke(state, new[] { cardModel, player });
                if (createdCard == null) continue;

                var result = System.Activator.CreateInstance(creationResultType, createdCard);
                if (result == null) continue;

                // 设置 entry 的 CreationResult 字段
                var entry = allCardEntries[injected];
                var entryType = entry.GetType();
                // CreationResult 有 private set，用 backing field
                var backingField = entryType.GetField("<CreationResult>k__BackingField",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (backingField != null)
                    backingField.SetValue(entry, result);
                else
                {
                    // 退而试属性
                    var resultProp = entryType.GetProperty("CreationResult",
                        BindingFlags.Public | BindingFlags.Instance);
                    resultProp?.SetValue(entry, result);
                }

                // 重算价格
                var calcMethod = entryType.GetMethod("CalcCost",
                    BindingFlags.Public | BindingFlags.Instance);
                calcMethod?.Invoke(entry, null);
                // 合成结果丢到原 _characterCardEntries 里，一行带一个 _price 创建于后面
                injected++;
            }

            // 注入到商店后同步消耗队列，避免战斗奖励重复（不消耗模式 ON 时跳过）
            if (!CardSpawnService.ReuseOnReroll)
            {
                while (CardSpawnService.SelectedCardIdsForReward.Count > 0)
                {
                    var consumed = CardSpawnService.ConsumeNextReplacement();
                    if (consumed != null)
                        CardRewardPatch.DiagLog($"ShopShop: consumed [{consumed}] from queue");
                }
            }

            GD.Print($"[SpawnCheat] Shop cards: injected {injected} marked");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpawnCheat] InjectMarkedCards failed: {ex.Message}");
        }
    }

    private static Type? FindTypeCached(string fullName)
    {
        if (_typeCache.TryGetValue(fullName, out var cached)) return cached;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName);
                if (t != null) { _typeCache[fullName] = t; return t; }
            }
            catch { }
        }
        _typeCache[fullName] = null;
        return null;
    }
}
