# MapOddsTracker

显示 STS2 所有 ACT 的预测节点顺序，支持自动切换当前 ACT 和手动查看其他 ACT。

## 功能

- ✅ 读取游戏种子
- ✅ 生成所有 ACT 的地图结构
- ✅ 小地图覆盖层显示
- ✅ 自动切换到当前 ACT
- ✅ 手动切换查看其他 ACT
- ❌ 不显示种子数值

## 技术原理

通过分析反编译的 sts2.dll，确定了以下类和方法：

| 游戏类 | 用途 |
|--------|------|
| `RunState.CreateForNewRun()` | 新游戏创建时捕获种子和状态 |
| `RunState.set_CurrentActIndex()` | ACT切换时更新当前索引 |
| `StandardActMap.CreateFor(RunState, bool)` | 根据种子和ACT生成确定性地图 |
| `ActMap.GetAllMapPoints()` | 获取地图所有节点 |
| `MapPoint.PointType` | 每个节点的类型（Monster/Elite/Boss等） |
| `MapPoint.coord` | 节点坐标（行列位置） |
| `RunRngSet.StringSeed` | 种子字符串 |
| `RunRngSet.Seed` | 种子哈希值，用于确定性随机生成 |

地图生成方式：`new Rng(runState.Rng.Seed, $"act_{actIndex + 1}_map")` — 每个ACT的RNG由基础种子+ACT编号确定。

## 安装

1. 确保已安装 **Godot 4.5.1 Mono** 和 **.NET 9.0 SDK**
2. 项目 `csproj` 已配置好路径（指向 `D:\Steam\steamapps\common\Slay the Spire 2\`）
3. 用 Godot 打开项目根目录（`MapOddsTracker/`）
4. 点击 **Create C# Solution**（右上角）
5. 编译：`dotnet build` 或 Godot 中按 `Ctrl+Shift+B`
6. 导出 PCK：Godot → Project → Export → Windows Desktop → Export

## 文件结构

```
MapOddsTracker/
├── MapOddsTracker.json    # MOD 配置
├── MapOddsTracker.csproj  # 项目文件（已配置路径）
├── Scripts/
│   ├── Entry.cs           # 入口点，初始化Harmony
│   ├── MapTracker.cs      # 核心逻辑（种子捕获、地图生成、NodeType枚举）
│   └── UI/
│       └── MapOverlay.cs  # 右上角覆盖层UI
└── README.md
```

## 参考资料

- [STS2 Modding Tutorial](https://glitchedreme.github.io/SlayTheSpire2ModdingTutorials)
- [Harmony Docs](https://harmony.pardeike.net/articles/basics.html)
- [GDRETools](https://github.com/GDRETools/gdsdecomp) - 反编译游戏资源
