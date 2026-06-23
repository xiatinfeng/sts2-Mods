using System.Diagnostics;
using Godot;

namespace ModSyncChecker.Scripts;

/// Platform detection and cross-platform utilities for mobile support (v2.4.1)
public static class PlatformHelper
{
    /// True on Android or iOS (mobile platforms requiring touch UI)
    public static bool IsMobile => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();

    /// True if user is on a touch-based device
    public static bool IsTouchDevice => IsMobile;

    /// Minimum touch target size per Google Material Design (dp)
    public static float TouchTargetSize => IsTouchDevice ? 44f : 0f;

    /// Open a folder in the system file manager, cross-platform
    public static void ShellOpenFolder(string folderPath)
    {
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            OS.ShellOpen(folderPath);
        }
        else
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folderPath,
                    UseShellExecute = true
                });
            }
            catch
            {
                OS.ShellOpen(folderPath);
            }
        }
    }
}
