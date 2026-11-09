using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace ModManager;

// One entry in the mod list. Mods without settings only show their name.
internal class ModInfo
{
    public string Name;
    public string Version;
    public string Kind;
    public string Detail;
    public ConfigFile Config;
    // Settings the plugin draws itself (see ModSettings.Register).
    public Action<ISettingsPage> Page;
    public bool ShowConfig = true;

    // The BepInEx config entries shown for this mod.
    public List<ConfigEntryBase> Entries =>
        ShowConfig && Config != null ? Config.Select(pair => pair.Value).ToList() : new List<ConfigEntryBase>();

    // The plugin's code, to read the tooltip texts it keeps for its settings rows.
    public System.Reflection.Assembly Assembly;
    // Names this plugin's rows in the game's settings window start with (see ModRegistry.OwnerOf).
    public List<string> Keys = new();
    // Rows the plugin added to the game's settings window; shown here through stand-in controls.
    public List<(GameObject Row, bool PerSave)> Adopted = new();

    public bool HasSettings => Page != null || Entries.Count > 0 || Adopted.Count > 0;
}

// Finds the installed mods: BepInEx plugins (they report their own name and version) and the
// custom tack, clothes and language files the game loads from Saves~/Mods (no code, so they are
// recognised by the item ids in their json files).
internal static class ModRegistry
{
    // Asset packs we know by name. A pack matches when an item id contains all of its terms
    // (case-insensitive). Add a line here for every new pack; unknown files are still listed,
    // grouped by folder.
    private static readonly KnownPack[] KnownPacks =
    {
        new("Westmore Ascot Jumping Pads", "Westmore Saddlery", "westmore", "ascot"),
        new("Tartan Saddle Pad - Complete Collection", "MaplePixel", "maplepixel", "tartan"),
    };

    private record KnownPack(string Name, string Author, params string[] Terms);

    private static readonly string[] AssetFolders = { "Tack", "Clothes", "Language" };

    // The game only reads these folders when it starts, so one look per session matches what is loaded.
    private static List<ModInfo> assetPacks;

    public static string ModsFolder
    {
        get
        {
            var path = DataManager.modsPath;
            return !string.IsNullOrEmpty(path) && Directory.Exists(path)
                ? path
                : Path.Combine(Application.persistentDataPath, "Saves~", "Mods");
        }
    }

    public static List<ModInfo> Collect()
    {
        var mods = new List<ModInfo>();
        try
        {
            mods.AddRange(Plugins());
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Listing the BepInEx plugins failed: {e}");
        }
        try
        {
            assetPacks ??= AssetPacks().ToList();
            mods.AddRange(assetPacks);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Listing the custom content packs failed: {e}");
        }
        return mods;
    }

    // Just the plugins, without looking at the disk.
    public static List<ModInfo> InstalledPlugins()
    {
        try
        {
            return Plugins().ToList();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Listing the BepInEx plugins failed: {e}");
            return new List<ModInfo>();
        }
    }

    private static IEnumerable<ModInfo> Plugins()
    {
        foreach (var info in IL2CPPChainloader.Instance.Plugins.Values)
        {
            var meta = info.Metadata;
            var page = ModSettings.Find(meta.GUID);
            var assembly = info.Instance?.GetType().Assembly.GetName().Name;
            yield return new ModInfo
            {
                Name = meta.Name,
                Version = meta.Version?.ToString(),
                Kind = "Plugin",
                Detail = meta.GUID,
                Config = (info.Instance as BepInEx.Unity.IL2CPP.BasePlugin)?.Config,
                Page = page?.Build,
                ShowConfig = page?.ShowConfigEntries ?? true,
                Assembly = info.Instance?.GetType().Assembly,
                // Plugins name the rows they add after themselves ("ImprovedStaff_Slider0"): by assembly,
                // plugin name or the last part of the guid. Short keys would match too much.
                Keys = new[] { assembly, meta.Name, meta.GUID.Split('.').Last() }
                    .Where(k => k != null).Select(Normalize).Where(k => k.Length >= 5).Distinct().ToList()
            };
        }
    }

    // Lower case letters and digits only, so "Improved Staff", "ImprovedStaff_Slider0" and "improvedstaff" compare.
    public static string Normalize(string text) =>
        new string((text ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // The plugin a row of the settings window belongs to: the one with the longest key the row's name starts with.
    public static ModInfo OwnerOf(string rowName, IEnumerable<ModInfo> plugins)
    {
        ModInfo owner = null;
        var length = 0;
        foreach (var plugin in plugins)
            foreach (var key in plugin.Keys)
                if (key.Length > length && StartsWithKey(rowName, key))
                {
                    owner = plugin;
                    length = key.Length;
                }
        return owner;
    }

    // The key must be followed by a non-letter and more name: a plugin called "Prestige" must not claim the
    // game's "PrestigeRewardMultiplier", and one called "Languages" must not claim the game's "Languages".
    private static bool StartsWithKey(string rowName, string key)
    {
        var matched = 0;
        var i = 0;
        for (; i < rowName.Length && matched < key.Length; i++)
        {
            if (!char.IsLetterOrDigit(rowName[i]))
                continue;
            if (char.ToLowerInvariant(rowName[i]) != key[matched])
                return false;
            matched++;
        }
        return matched == key.Length && i < rowName.Length && !char.IsLetter(rowName[i]);
    }

    private static IEnumerable<ModInfo> AssetPacks()
    {
        var root = ModsFolder;
        if (!Directory.Exists(root))
        {
            Plugin.Logger.LogWarning($"The game's mods folder wasn't found ({root}), so custom content is not listed.");
            yield break;
        }
        foreach (var folder in AssetFolders)
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir))
                continue;

            var known = new Dictionary<KnownPack, int>();
            var other = 0;
            // Translations aren't json files; every file in that folder counts.
            foreach (var file in Directory.EnumerateFiles(dir, folder == "Language" ? "*" : "*.json", SearchOption.AllDirectories))
            {
                var id = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ReadId(file) : null;
                var pack = id == null ? null : KnownPacks.FirstOrDefault(p => p.Terms.All(t => id.Contains(t, StringComparison.OrdinalIgnoreCase)));
                if (pack == null)
                    other++;
                else
                    known[pack] = known.GetValueOrDefault(pack) + 1;
            }

            foreach (var (pack, count) in known)
                yield return new ModInfo
                {
                    Name = pack.Name,
                    Kind = $"Custom {folder.ToLowerInvariant()}",
                    Detail = $"by {pack.Author}, {Items(count)}"
                };
            if (other > 0)
                yield return new ModInfo
                {
                    Name = $"Other custom {folder.ToLowerInvariant()}",
                    Kind = $"Custom {folder.ToLowerInvariant()}",
                    Detail = Items(other)
                };
        }
    }

    private static string Items(int count) => count == 1 ? "1 item" : $"{count} items";

    private static string ReadId(string file)
    {
        try
        {
            var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
            using var doc = JsonDocument.Parse(File.ReadAllText(file), options);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var id)
                ? id.GetString()
                : null;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Could not read {file}: {e.Message}");
            return null;
        }
    }
}
