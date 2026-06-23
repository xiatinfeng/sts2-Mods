# ModSyncChecker — 副本说明

## 权威开发目录
**`tools/modsync_godot/`** 是主开发目录，所有修改请在此进行。

## 本目录
`sts2-Mods/ModSyncChecker/` 是 sts2-Mods 仓库内的副本，
用于统一管理 StS2 MOD 源码时的目录结构对齐。

**不要直接编辑本目录中的文件。**
修改请到 `WorkBuddy/Claw/tools/modsync_godot/`，然后同步过来。

## 同步
如果 modsync_godot 有更新，运行:
```
xcopy /E /Y tools\modsync_godot\* sts2-Mods\ModSyncChecker\
```

## git 说明
- modsync_godot: 独立 git 仓库 (master)
- sts2-Mods: 三个 MOD 的聚合仓库 (master + monster-action-predictor)
- 两者有独立的 git 历史，不共享 commit
