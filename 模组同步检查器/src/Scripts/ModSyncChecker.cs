using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using ModSyncChecker.Scripts.UI;

namespace ModSyncChecker.Scripts;

/// <summary>
/// MOD 初始化入口 - 使用 ModInitializerAttribute 让游戏自动调用
/// 注意：必须是 public class（非 static），和 MapOddsTracker 保持一致
/// </summary>
[ModInitializer("Init")]
public class ModSyncChecker
{
    private static ModSyncPanel? _panel;
    private static bool _initialized;
    private static CanvasLayer? _canvasLayer;
    // v2.3.7: _configNode removed — ModSyncConfigNode is now static-only (no BaseLib inheritance).
    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;

        FileLogger.Init();
        FileLogger.Info("v2.4.3 Init() called — BaseLib optional");

        GD.Print(L.T("InitSuccess"));

        // v2.3.7: Load config from config.json (always works, no BaseLib required).
        // BaseLib integration is optional — tried via reflection in TryInitBaseLibBridge().
        ModSyncConfigNode.LoadFromConfig();
        GD.Print("[ModSyncChecker] Config loaded from config.json.");

        // v2.3.7: Attempt BaseLib config UI bridge (optional, via pure reflection).
        TryInitBaseLibBridge();

        // 尝试立即创建面板（如果场景树已就绪）
        CreatePanel();

        try
        {
            var harmony = new Harmony("sts2.user.modsyncchecker");
            harmony.PatchAll();
            GD.Print(L.T("PatchApplied"));
        }
        catch (Exception ex)
        {
            GD.PrintErr(L.TF("PatchFailedFmt", ex.Message));
        }
    }

    public static void CreatePanel()
    {
        try
        {
            var sceneTree = Engine.GetMainLoop() as SceneTree;
            var root = sceneTree?.Root;
            if (root == null)
            {
                GD.PrintErr("[ModSyncChecker] Root is null, cannot create panel yet.");
                return;
            }

            // v2.4.2: Move GetNodeOrNull check INSIDE CallDeferred to prevent race.
            // Init() and NMainMenu_Ready_Patch.Postfix can both call CreatePanel()
            // before the deferred callback executes, causing double add_child → SIGSEGV.
            Callable.From(() =>
            {
                try
                {
                    var existing = root.GetNodeOrNull("ModSyncCheckerLayer");
                    if (existing != null) return;

                    _canvasLayer = new CanvasLayer();
                    _canvasLayer.Name = "ModSyncCheckerLayer";
                    _canvasLayer.Layer = 100; // 高层级，确保在最上面
                    root.AddChild(_canvasLayer);

                    _panel = new ModSyncPanel();
                    _panel.Name = "ModSyncCheckerPanel";
                    _canvasLayer.AddChild(_panel);

                    GD.Print(L.T("PanelCreated"));
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[ModSyncChecker] Deferred panel creation failed: {ex.Message}");
                }
            }).CallDeferred();
        }
        catch (Exception ex)
        {
            GD.PrintErr(L.TF("PanelCreateFailedFmt", ex.Message));
        }
    }

    public static void ShowDiffPanel(List<string> remoteMods, List<string>? missingOnHost, List<string>? missingOnLocal)
    {
        if (_panel == null || !GodotObject.IsInstanceValid(_panel))
        {
            CreatePanel();
        }
        Callable.From(() => _panel?.ShowPanel(remoteMods, missingOnHost, missingOnLocal)).CallDeferred();
    }

    public static void ShowPanelManual()
    {
        try
        {
            if (_panel == null || !GodotObject.IsInstanceValid(_panel))
            {
                CreatePanel();
            }
            Callable.From(() => _panel?.ShowPanel()).CallDeferred();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModSyncChecker] ShowPanelManual failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Attempt to bridge ModSyncConfigNode settings into BaseLib's mod config UI
    /// via pure reflection. If BaseLib is not installed, this method silently returns.
    /// Never throws — all exceptions are caught and ignored.
    /// </summary>
    private static void TryInitBaseLibBridge()
    {
        try
        {
            // Check if BaseLib types are available (soft dependency).
            var registryType = Type.GetType("BaseLib.API.ModConfigRegistry, BaseLib");
            var attrType = Type.GetType("BaseLib.ModConfigAttribute, BaseLib");
            if (registryType == null || attrType == null)
                return;

            // Register ModSyncConfigNode's static config properties (FontScale, UseImportModeAsDefault)
            // with BaseLib so they appear in the mod settings panel when BaseLib is installed.
            var registerConfig = registryType.GetMethod("RegisterConfig",
                BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(Type) }, null);

            if (registerConfig != null)
            {
                registerConfig.Invoke(null, new object[] { typeof(ModSyncConfigNode) });
                GD.Print("[ModSyncChecker] BaseLib config bridge initialized.");
            }
        }
        catch (Exception ex)
        {
            // BaseLib integration is optional — log and ignore any failure.
            FileLogger.Warn($"[ModSyncChecker] BaseLib bridge init failed: {ex.Message}");
        }
    }
}

#region Harmony Patches

/// <summary>
/// Patch NMapScreen.Open Postfix to create panel when scene tree is ready
/// (Same pattern as MapOddsTracker)
/// </summary>
[HarmonyPatch(typeof(NMapScreen), "Open")]
public static class NMapScreen_Open_Patch
{
    public static void Postfix()
    {
        try
        {
            ModSyncChecker.CreatePanel();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModSyncChecker] NMapScreen.Open Postfix failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Patch NMainMenu._Ready Postfix to:
/// 1. Create the ModSyncChecker panel early (so F8 works in main menu)
/// 2. Add a "MOD SYNC" button to the main menu
/// </summary>
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class NMainMenu_Ready_Patch
{
    public static void Postfix(NMainMenu __instance)
    {
        try
        {
            if (__instance == null) return;

            // 1. 尽早创建面板，确保主菜单阶段 F8 也能工作
            ModSyncChecker.CreatePanel();

            // 2. v2.4.2: Wrap button creation in CallDeferred to avoid
            // race with Godot render thread on Android.
            Callable.From(() =>
            {
                try
                {
                    // 在主菜单添加自定义按钮
                    var buttonContainer = __instance.GetNode<Control>("MainMenuTextButtons");
                    if (buttonContainer == null) return;

                    // 避免重复添加
                    var existingButton = buttonContainer.GetNodeOrNull<NMainMenuTextButton>("ModSyncCheckerButton");
                    if (existingButton != null) return;

                    // 以 SettingsButton 为模板复制
                    var template = __instance.GetNode<NMainMenuTextButton>("MainMenuTextButtons/SettingsButton");
                    if (template == null) return;
                    var myButton = (NMainMenuTextButton)template.Duplicate();
                    myButton.Name = "ModSyncCheckerButton";

                    // 设置按钮文本
                    var label = myButton.GetChild<Label>(0);
                    if (label != null)
                    {
                        label.Text = L.T("MainMenuButton");
                    }

                    // 连接点击事件
                    myButton.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ =>
                    {
                        ModSyncChecker.ShowPanelManual();
                    }));

                    // 插入到 QuitButton 之前
                    var quitButton = __instance.GetNode<NMainMenuTextButton>("MainMenuTextButtons/QuitButton");
                    buttonContainer.AddChild(myButton);
                    buttonContainer.MoveChild(myButton, quitButton.GetIndex());

                    GD.Print(L.T("MenuButtonAdded"));
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[ModSyncChecker] NMainMenu deferred button creation failed: {ex.Message}");
                }
            }).CallDeferred();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModSyncChecker] NMainMenu._Ready Postfix failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Patch JoinFlow.Begin Finalizer to catch connection failures and show mod differences
/// </summary>
[HarmonyPatch(typeof(JoinFlow), "Begin")]
public static class JoinFlow_Begin_Patch
{
    private static readonly Type? ConnectionFailureExtraInfoType;
    private static readonly FieldInfo? MissingModsOnHostField;
    private static readonly FieldInfo? MissingModsOnLocalField;
    private static readonly FieldInfo? ExtraInfoField;

    static JoinFlow_Begin_Patch()
    {
        try
        {
            ConnectionFailureExtraInfoType = Type.GetType("MegaCrit.Sts2.Core.Multiplayer.Connection.ConnectionFailureExtraInfo, sts2");
            if (ConnectionFailureExtraInfoType != null)
            {
                MissingModsOnHostField = ConnectionFailureExtraInfoType.GetField("missingModsOnHost");
                MissingModsOnLocalField = ConnectionFailureExtraInfoType.GetField("missingModsOnLocal");
            }

            var netErrorInfoType = Type.GetType("MegaCrit.Sts2.Core.Multiplayer.Connection.NetErrorInfo, sts2");
            ExtraInfoField = netErrorInfoType?.GetField("extraInfo");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModSyncChecker] Patch init failed: {ex.Message}");
        }
    }

    public static Exception? Finalizer(Exception? __exception)
    {
        if (__exception == null) return null;

        try
        {
            var exceptionType = __exception.GetType();
            if (exceptionType.Name != "ClientConnectionFailedException")
                return __exception;

            var netErrorInfoProp = exceptionType.GetProperty("NetErrorInfo");
            var netErrorInfo = netErrorInfoProp?.GetValue(__exception);
            if (netErrorInfo == null) return __exception;

            var reasonProp = netErrorInfo.GetType().GetProperty("Reason");
            var reason = reasonProp?.GetValue(netErrorInfo);

            string? reasonStr = reason?.ToString();
            if (reasonStr != "ModMismatch" && reasonStr != "VersionMismatch")
                return __exception;

            GD.Print(L.TF("ConnectionFailedFmt", reasonStr));

            List<string> remoteMods = new();
            List<string> missingOnHost = new();
            List<string> missingOnLocal = new();

            var extraInfo = ExtraInfoField?.GetValue(netErrorInfo);
            if (extraInfo != null)
            {
                var hostMissing = MissingModsOnHostField?.GetValue(extraInfo) as List<string>;
                var localMissing = MissingModsOnLocalField?.GetValue(extraInfo) as List<string>;

                if (hostMissing != null) missingOnHost = hostMissing;
                if (localMissing != null) missingOnLocal = localMissing;
            }

            if (remoteMods.Count == 0)
            {
                var localMods = ModSyncCore.ScanLocalMods();
                remoteMods = localMods
                    .Where(m => m.AffectsGameplay && !missingOnLocal.Contains(m.Id))
                    .Select(m => $"{m.Id}-{m.Version}")
                    .ToList();
                remoteMods.AddRange(missingOnHost.Select(id => $"{id}-unknown"));
            }

            ModSyncChecker.ShowDiffPanel(remoteMods, missingOnHost, missingOnLocal);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModSyncChecker] Finalizer failed: {ex.Message}");
        }

        return __exception;
    }
}

#endregion
