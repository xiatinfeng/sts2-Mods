using Godot;

namespace MapOddsTracker.Scripts;

/// <summary>
/// Thin wrapper around GD.Print / GD.PrintErr that always includes the
/// standard mod log-prefix, so callers never repeat the magic string.
/// </summary>
internal static class ModLogger
{
    public static void Log(string message) =>
        GD.Print($"{ModConstants.LogPrefix} {message}");

    public static void LogErr(string message) =>
        GD.PrintErr($"{ModConstants.LogPrefix} {message}");
}
