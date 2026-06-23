# ADR-001: 补丁策略选择 — Postfix vs Transpiler

## 状态

accepted
日期: 2026-06-18

## 背景与问题

STS2 的 `CardReward.OnSelect()` 是 `protected override async Task<bool>` 方法，其 recording 代码写在 async 状态机的后半段。Harmony 需要注册 Postfix 来清洗 recording 写入的 `CardChoices`。

问题是：对于 async 方法，Harmony 的 Postfix 在状态机启动时（recording 尚未执行）还是 Task 完成后（recording 已执行）触发？该用 Postfix 还是 Transpiler？

## 决策驱动因素

* **隐蔽性** — 方案不能破坏游戏的正常逻辑，不能留下异常痕迹
* **稳定性** — 方案必须优雅处理 Harmony 版本的兼容性差异
* **可维护性** — 代码复杂度越低越好，IL 操作越少越好
* **安全降级** — 方案不支持时应无影响地跳过，而非崩溃

## 考虑过的方案

* **方案 A**: Transpiler — 在 IL 层面替换 `ldfld _cards` 指令，使 removeReward 循环使用缓存快照
* **方案 B**: Postfix（async 延迟） — Harmony 2.3+ 对 async 方法的 Postfix 自动延迟到 Task 完成后触发，在 Postfix 中重建 CardChoices
* **方案 C**: 在 `OnSelect` 尾部通过 Transpiler 插入清理方法的调用

## 决策结果

选择的方案: **方案 B（Postfix）**

理由: 
- 方案 A 需要处理 `List<CardCreationResult>` 与 `List<object>` 的 IL 类型不兼容问题，复杂度高，易出错
- 方案 C 需要找到 MoveNext 的 ret 位置并插入 IL 调用，同样复杂
- 方案 B 代码直观：读取 CardChoices → 重建为 3 条 → 写回
- 方案 B 在不支持 async Postfix 的旧 Harmony 版本上安全工作（recording 未执行时 choices 为空 → 跳过）
- Harmony 2.4.2（STS2 当前版本）已稳定支持 async Postfix

### 正面后果

* 代码可读性高，逻辑集中在 Postfix 方法内
* 安全降级：Postfix 在 recording 之前触发时，CardChoices 为空 → 跳过清洗
* 不需要 IL 操作，避免验证错误

### 负面后果 / 需注意

* 依赖 Harmony 2.3+ 的 async 方法支持（当前 STS2 使用 2.4.2，无问题）
* 如果将来降到 2.2 以下版本，清洗功能会静默失效

### 被否决的方案

**方案 A（Transpiler）**：IL 层面替换 `ldfld _cards` 需要维护栈平衡，且 `List<CardCreationResult>` 与 `List<object>` 类型不兼容会导致 PEVerify 错误。调试困难，不必要地复杂。

**方案 C（尾部插入）**：需要在 async 状态机 MoveNext 的 ret 指令前插入清理调用，需要精确定位 IL 位置，与方案 A 一样复杂且不必要。

## 更多信息

关联代码：`Scripts/Patches/CardRewardPatch.cs` → `OnSelectPatch.Postfix`

后续若遇到 Harmony 版本兼容问题，可回退到方案 A，但当前版本无此必要。
