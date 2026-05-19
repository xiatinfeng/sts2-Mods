using System;
using Godot;

namespace ModSyncChecker.Scripts;

/// <summary>
/// v2.3.7: Removed BaseLib hard dependency (SimpleModConfig inheritance).
/// Config is loaded from config.json via ModSyncCore.LoadConfigFromFile().
/// BaseLib integration (config UI) is now optional, bridged via reflection
/// in ModSyncChecker.TryInitBaseLibBridge().
/// </summary>
public partial class ModSyncConfigNode
{
    private const float FontScaleEpsilon = 0.001f;
    public const string PanelTypeImport = "import";
    public const string PanelTypeEncoding = "encoding";

    private static float s_fontScale = 1.0f;
    private static bool s_useImportMode;
    private static bool s_loadedFromConfigJson;

    public static float FontScale
    {
        get => s_fontScale;
        set
        {
            if (Math.Abs(s_fontScale - value) > FontScaleEpsilon)
            {
                s_fontScale = value;
                ModSyncCore.UpdateFontScale(value);
            }
        }
    }

    public static bool UseImportModeAsDefault
    {
        get => s_useImportMode;
        set
        {
            if (s_useImportMode != value)
            {
                s_useImportMode = value;
                var panel = value ? PanelTypeImport : PanelTypeEncoding;
                ModSyncCore.UpdateDefaultPanel(panel);
            }
        }
    }

    /// <summary>
    /// Load settings from config.json. Must be called before accessing properties.
    /// </summary>
    public static void LoadFromConfig()
    {
        if (s_loadedFromConfigJson) return;

        var config = ModSyncCore.LoadConfigFromFile();
        if (config != null)
        {
            FontScale = config.FontScale;
            UseImportModeAsDefault = config.DefaultPanel == PanelTypeImport;
        }
        s_loadedFromConfigJson = true;
    }

    /// <summary>
    /// v2.3.7: Resets loaded flag for config reload (e.g. after import).
    /// </summary>
    public static void MarkDirty()
    {
        s_loadedFromConfigJson = false;
    }
}
