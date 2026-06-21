# sts2-spawn-cheat

Slay the Spire 2 隐形作弊 MOD — 队列消耗式替换，零历史痕迹。

## 功能

### 队列消耗式替换
- **卡牌**：每次战斗奖励消耗队列头一张，替换奖励第一张
- **遗物**：每次遗物奖励消耗队列头一个
- **药水**：每次药水奖励消耗队列头一个
- **商店**：标记的物品出现在商店，可选消耗队列或保留

### 遮罩守卫
场景切换导致面板可见性重置时，自动强制隐藏（0.1s 轮询）。

### 不消耗模式
开关位于队列 Tab 底部。开启后：
- 商店注入但不消耗队列（留给战斗奖励）
- 战斗奖励刷新按钮复用同一张卡

### 静默刷新
刷新按钮绕过游戏 `CardReward.Reroll()`，不记录 `CardChoices` 历史，不留痕迹。

### 收藏集
命名保存当前队列，支持追加/覆盖/删除。持久化到 JSON 文件。

### 快捷键
- `F5`：开/关面板
- `ESC`：关闭面板

## 版本历史

| 版本 | 亮点 |
|:----|:-----|
| v1.2.0 | 附模悬停提示 + 动态扫描 + 中文名 |
| v1.1.0 | 队列四列布局（附魔/升级列） |
| v1.0.0 | 首次稳定版，队列消耗式替换 + 静默刷新 + 商店注入 |

## 安装

1. 编译 DLL：
```bash
cd tools/sts2-spawn-cheat
dotnet build
```
2. 复制 `bin/Debug/sts2-spawn-cheat.dll` 到游戏 mods 目录
3. 启动游戏，F5 打开面板

## 存档路径

- 收藏集：`%APPDATA%/Godot/app_userdata/Slay the Spire 2/spawn_cheat_collections.json`

## 技术架构

```
SpawnCheatMod.cs           — MOD 入口，注册所有 Harmony Patch
├── CardRewardPatch.cs     — Populate Postfix：卡牌替换
├── RelicRewardPatch.cs    — Populate Postfix：遗物替换
├── PotionRewardPatch.cs   — Populate Postfix：药水替换
├── CardRewardRerollPatch  — 刷新按钮注入 + 静默刷新
├── MerchantRefreshPatch   — 商店刷新按钮
└── SpawnCheatPanel.cs     — 面板 UI（6 个 Tab）

CardSpawnService.cs        — 核心数据层
├── Queue 系统（卡牌/遗物/药水）
├── 收藏集系统（CollectionSet）
└── 收藏系统（Favorites）

ShopSpawnService.cs        — 商店注入
```

## 文件结构

```
Scripts/
├── SpawnCheatMod.cs
├── Core/
│   ├── CardSpawnService.cs
│   ├── RelicSpawnService.cs
│   └── ShopSpawnService.cs
├── Patches/
│   ├── CardRewardPatch.cs
│   ├── CardRewardRerollPatch.cs
│   ├── RelicRewardPatch.cs
│   ├── PotionRewardPatch.cs
│   └── MerchantRefreshPatch.cs
└── UI/
    ├── SpawnCheatPanel.cs
    └── InputHandler.cs
```
