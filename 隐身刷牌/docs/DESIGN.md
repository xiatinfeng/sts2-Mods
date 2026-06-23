# sts2-spawn-cheat — 设计文档

## 核心架构

### 队列消耗式替换（v3.0.0+）

```
标记 → 队列 → 每次 Populate 消耗一张 → 替换原生
```

取代了早期的"3+N 显示 + 历史清洗"方案。核心优势：

1. **CardChoices 始终 3 条** — 完全自然，不需要任何历史清洗
2. **零痕迹** — 游戏原生的 3 挑 1 机制未变
3. **简单可靠** — 每场战斗只需替换 `_cards[0]`

### 队列实现

```csharp
// 每个类型独立队列
Queue<string> _replacementQueue  // 卡牌
Queue<string> _relicReplacementQueue  // 遗物
Queue<string> _potionReplacementQueue  // 药水

// 消费方法
ConsumeNextReplacement()  // 卡牌
ConsumeNextRelic()        // 遗物
ConsumeNextPotion()       // 药水
```

每个消费方法：
1. Dequeue 头项
2. 从标记列表移除（同步清除）
3. Rebuild 队列（反映最新状态）
4. 返回消费的 ID

### 脏标记（v3.12.0）

`CardSpawnService.QueueDirty` — 队列被消耗时置为 `true`，守卫定时器检测到后刷新队列 Tab。避免每 0.1s 全量重建导致的按钮点击丢失。

### 遮罩守卫

```csharp
// 每 0.1s 检查面板可见性
guardTimer.Timeout += () => {
    if (_rootPanel.Visible != _isVisible)  // 修正
    if (_activeTabIdx == 5 && QueueDirty)  // 脏刷新
};
```

### 静默刷新（v3.11.0）

刷新按钮绕过 `CardReward.Reroll()`，直接清 `_cards` → `Populate()`。不调 `SyncLocalSkippedCard`，不写 `CardChoiceHistoryEntry`。

### Harmony Patch 注册

```csharp
// SpawnCheatMod.cs
RegisterCardRewardPatches();     // Populate Postfix
RegisterCardRewardRerollPatch(); // 刷新按钮注入
RegisterMerchantRefreshPatch();  // 商店刷新
RelicRewardPatch.Register();     // 遗物替换
PotionRewardPatch.Register();    // 药水替换
CardSpawnService.InitCollections(); // 加载收藏集
RegisterNGameReadyPatch();       // 初始化
```

## 关键决策

| 决策 | 版本 | 理由 |
|:----|:----|:-----|
| 1:1 替换替代 3+N 清洗 | v3.0.0 | 更简单，零历史痕迹 |
| RefreshUi 延迟 0.05s | v3.5.0 | 避免遮罩残留 |
| 守卫定时器 | v3.5.0 | 修复场景切换面板可见性重置 |
| 静默刷新 | v3.11.0 | 防止 Reroll 在 CardChoices 留痕 |
| ReuseOnReroll | v3.4.0 | 可配置刷新是否消耗队列 |
| 商店队列消耗 | v3.9.5 | 防止商店+战斗奖励重复出现 |
| 收藏集追加 | v3.8.0 | 不顶替现有标记 |
| 脏标记代替轮询 | v3.12.0 | 修复按钮点击丢失 |
| 附魔/升级支持 | v1.2.0 | ModelDb.Enchantment<T>() 获取 canonical → ToMutable → EnchantInternal |
| 动态附魔发现 | v1.2.0 | 扫描全局程序集 EnchantmentModel 子类 |
| Tooltip 悬停提示 | v1.2.0 | DynamicDescription.GetFormattedText() + _CleanDesc |
| ModConfig 集成 | v1.4.0 | 软依赖 ModConfig，注册快捷键 + 隐藏模组开关 |
| 蒸汽云存档 | v1.4.3 | SetValue/GetValue 通过 ModConfig 自动同步 |
| 网格内联更新 | v1.5.0 | 点击标记只改 StyleBoxFlat，不重建全网格 |
| Refresh 延迟重建 | v1.5.0 | 和卡牌一致：QueueFree 后 0.05s 延时重建 |
| 日志路径统一 | v1.5.4 | debug.log 移到 %APPDATA%/Godot/... |
