using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Godot;

namespace MapOddsTracker.Scripts;

/// <summary>
/// Maps monster English IDs to local bundled image paths.
/// Supports .png and .webp in assets/monsters/, assets/bosses/, and assets/manual/
/// </summary>
public static class MonsterImageMapper
{
    private static readonly string AssetsBaseDir;

    private const string BossesDir = "bosses";
    private const string MonstersDir = "monsters";
    private const string ManualDir = "manual";

    private static readonly string[] ImageExtensions = { ".png", ".webp" };

    // Boss IDs discovered at runtime from act._rooms boss encounter data.
    // Populated by MapTracker.EnsureGenerated() via RegisterBossIds().
    private static readonly HashSet<string> BossIds = new(StringComparer.OrdinalIgnoreCase);
    private static bool _bossIdsRegistered = false;

    /// <summary>
    /// Register boss monster IDs discovered at runtime from act._rooms data.
    /// Called by MapTracker.EnsureGenerated() after encounter queues are built.
    /// Safe to call multiple times — only first call takes effect.
    /// </summary>
    public static void RegisterBossIds(IEnumerable<string> ids)
    {
        if (_bossIdsRegistered || ids == null) return;
        foreach (var id in ids)
        {
            if (!string.IsNullOrWhiteSpace(id))
                BossIds.Add(id.Trim().ToLowerInvariant());
        }
        _bossIdsRegistered = true;
        GD.Print($"[MapOddsTracker] Registered {BossIds.Count} boss IDs from runtime data.");
    }

    // Non-standard filename overrides: key = monster ID, value = filename stem (no extension).
    // Only add entries when the asset filename differs from the monster's internal ID.
    // Identity mappings (where filename == monster ID) use the default path and are NOT listed here.
    private static readonly Dictionary<string, string> SpecialFileMappings = new(StringComparer.OrdinalIgnoreCase)
    {
    };

    // Cache of files in the manual folder: key = lowercase filename without extension, value = full path
    private static readonly Dictionary<string, string> ManualFileCache = new(StringComparer.OrdinalIgnoreCase);

    static MonsterImageMapper()
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var assemblyDir = Path.GetDirectoryName(assemblyPath);
        AssetsBaseDir = assemblyDir != null ? Path.Combine(assemblyDir, "assets") : string.Empty;

        GD.Print($"[MapOddsTracker] MonsterImageMapper initialized. AssetsBaseDir={AssetsBaseDir}");
        ScanManualFolder();
    }

    private static void ScanManualFolder()
    {
        if (string.IsNullOrEmpty(AssetsBaseDir))
        {
            GD.PrintErr("[MapOddsTracker] AssetsBaseDir is empty, cannot scan manual folder.");
            return;
        }

        var manualPath = Path.Combine(AssetsBaseDir, ManualDir);
        GD.Print($"[MapOddsTracker] Scanning manual folder: {manualPath}");

        if (!Directory.Exists(manualPath))
        {
            GD.PrintErr($"[MapOddsTracker] Manual folder does not exist: {manualPath}");
            return;
        }

        try
        {
            int count = 0;
            foreach (var file in Directory.EnumerateFiles(manualPath))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!ImageExtensions.Contains(ext)) continue;

                var stem = Path.GetFileNameWithoutExtension(file);
                ManualFileCache[stem] = file;
                count++;
                GD.Print($"[MapOddsTracker] Cached manual image: {stem} -> {file}");
            }
            GD.Print($"[MapOddsTracker] Manual folder scan complete. {count} images cached.");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MapOddsTracker] Failed to scan manual folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Get the absolute file path for a monster image by its English ID.
    /// Returns null if no image exists.
    /// </summary>
    public static string? GetImagePath(string englishId, bool isBoss = false)
    {
        if (string.IsNullOrWhiteSpace(englishId) || string.IsNullOrEmpty(AssetsBaseDir))
            return null;

        var id = englishId.Trim().ToLowerInvariant();

        // Boss images: check bosses/{id}_boss.{ext}
        if (isBoss || BossIds.Contains(id))
        {
            foreach (var ext in ImageExtensions)
            {
                var bossPath = Path.Combine(AssetsBaseDir, BossesDir, $"{id}_boss{ext}");
                if (File.Exists(bossPath)) return bossPath;
            }
        }

        // Special file mappings in monsters/
        if (SpecialFileMappings.TryGetValue(id, out var specialStem))
        {
            foreach (var ext in ImageExtensions)
            {
                var specialPath = Path.Combine(AssetsBaseDir, MonstersDir, $"{specialStem}{ext}");
                if (File.Exists(specialPath)) return specialPath;
            }
        }

        // Default: monsters/{id}.{ext}
        foreach (var ext in ImageExtensions)
        {
            var defaultPath = Path.Combine(AssetsBaseDir, MonstersDir, $"{id}{ext}");
            if (File.Exists(defaultPath)) return defaultPath;
        }

        // Fallback: manual folder (exact stem match, case-insensitive)
        if (ManualFileCache.TryGetValue(id, out var manualPath))
        {
            GD.Print($"[MapOddsTracker] Found manual image for '{id}': {manualPath}");
            return manualPath;
        }

        // Last resort: manual folder partial match (e.g. "decimillipede" -> "decimillipede_segment")
        foreach (var kvp in ManualFileCache)
        {
            if (kvp.Key.Contains(id, StringComparison.OrdinalIgnoreCase) || id.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                GD.Print($"[MapOddsTracker] Found partial manual match for '{id}': {kvp.Value}");
                return kvp.Value;
            }
        }

        GD.Print($"[MapOddsTracker] No image found for '{id}' (isBoss={isBoss})");
        return null;
    }

    public static bool HasImage(string englishId, bool isBoss = false)
    {
        return GetImagePath(englishId, isBoss) != null;
    }
}
