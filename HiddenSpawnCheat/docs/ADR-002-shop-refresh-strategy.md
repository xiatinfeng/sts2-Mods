# ADR-002: 商店刷新补丁策略 — Patch Initialize 而非 _Ready

## 状态

accepted  
日期: 2026-06-19

## 背景与问题

商店界面需要注入「🔄 刷新」按钮，点击后重建整个商店库存并刷新 UI。

最初的方案是 Patch `NMerchantInventory._Ready()` Postfix，但存在两个问题：

1. **`_Ready` 触发时机不稳定** — 商店打开时 `_Ready` 已经调用过，补丁注册后才触发导致按钮不出现
2. **"Merchant inventory already populated"** — 刷新时需要重新初始化 UI，但 `Initialize()` 第二次调用会抛 `InvalidOperationException`

## 决策驱动因素

* **可靠性** — 按钮必须在每次商店打开时出现
* **正确性** — 刷新后 UI 必须完整重建
* **可维护性** — 方案应尽可能简单，避免过度工程

## 考虑过的方案

### 方案 A: Patch `_Ready()` Postfix

直接在 `_Ready` 中注入按钮。刷新时通过反射替换 NMerchantInventory 内部字段。

| 维度 | 评价 |
|:----|:----:|
| 可靠性 | ❌ `_Ready` 触发后注册就无效了 |
| 刷新后 UI | ❌ 内部字段替换后 UI 不刷新 |
| 复杂度 | ⭐ 简单 |

### 方案 B: Patch `Initialize()` Postfix（煎包方案 ✅ 最终选择）

Patch `NMerchantInventory.Initialize(MerchantInventory, MerchantDialogueSet)` Postfix，在 Postfix 中注入按钮。
刷新时：将 NMerchantInventory 的 `<Inventory>k__BackingField` 设 null → 调 `Initialize()` → Postfix 触发。

| 维度 | 评价 |
|:----|:----:|
| 可靠性 | ✅ `Initialize` 在每次创建 UI 时必然触发 |
| 刷新后 UI | ✅ 完整重建 |
| 复杂度 | ⭐⭐ 需处理事件解绑和信号断开 |

### 方案 C: 直接替换场景节点

销毁旧 NMerchantInventory 节点，创建全新实例。

| 维度 | 评价 |
|:----|:----:|
| 可靠性 | ⭐⭐ 可行但繁琐 |
| 刷新后 UI | ✅ 全新节点 |
| 复杂度 | ⭐⭐⭐ 需要处理场景树引用和状态 |

## 决策结果

选择的方案: **方案 B（Patch Initialize Postfix）**

理由:
- 煎包 RefreshShop v0.1.2 已验证此方案在生产环境中稳定工作
- `Initialize` 是 Godot 节点的标准生命周期，Patch 后 100% 触发
- 反编译验证了完整的实现细节（包括事件解绑和信号断开）

### 正面后果

* 按钮注入时机可靠
* 刷新流程清晰：设 null → Initialize → Postfix 重新注入
* 与煎包方案一致，方便参照

### 需注意

* 刷新前必须 `UnsubscribeOldEntries` + `DisconnectOldSlotSignals`，否则内存泄露
* 必须重置 `<Inventory>k__BackingField` 为 null，否则 `Initialize` 抛 "already populated"
* 需要 `IsRefreshing` 标志防止刷新时 Postfix 重复注入按钮

## 标记系统 → 商店扩展

当前标记系统（`SelectedCardIdsForReward` / `MarkedRelicIds` / `MarkedPotionIds`）仅支持 CardReward Populate 注入。
后续扩展至商店时，需要在 `CreateForNormalMerchant` 后把标记物品替换进新库存。

## 更多信息

- 关联代码: `Scripts/Patches/MerchantRefreshPatch.cs` → `MerchantRefreshPatch`
- 关联代码: `Scripts/Core/ShopSpawnService.cs` → `RefreshShopOnNode()`
- 参考项目: `tools/Jianbao233-RefreshShop/` → `ShopRefreshService.RebuildShop()`
- 反编译工具: `ilspycmd` → `RefreshShop.dll`
