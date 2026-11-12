using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
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

    // On/off (see ModSwitch). The file names on disk are the state; nothing else is stored.
    public string Guid;
    // Plugin: its DLL, named without ".off" even while it is off.
    public string File;
    // Pack: "Tack", "Clothes" or "Language", and the files it is made of, wherever they are now.
    public string Folder;
    public List<string> Files = new();
    public bool On = true;
    // Whether the game loaded it; a switch only takes effect at the next start.
    public bool OnAtStart = true;
    public bool CanSwitch;
    // Plugins this one can't load without.
    public List<string> Needs = new();
}

// Finds the installed mods: BepInEx plugins (they report their own name and version), plugins that are
// switched off, and the custom tack, clothes and language files the game loads from Saves~/Mods (no code,
// so they are recognised by the item ids in their json files).
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

    internal const string OffSuffix = ".off";

    // A folder that can't be read is skipped instead of hiding the whole list.
    private static readonly EnumerationOptions AllFiles = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };

    // Which packs were in place when the game started (it only reads the folders then).
    private static Dictionary<string, bool> packsAtStart;

    public static string ModsFolder
    {
        get
        {
            var path = DataManager.modsPath;
            return Path.GetFullPath(!string.IsNullOrEmpty(path) && Directory.Exists(path)
                ? path
                : Path.Combine(Application.persistentDataPath, "Saves~", "Mods"));
        }
    }

    // Packs that are switched off wait here, in the same Tack/Clothes/Language folders; the game doesn't read it.
    public static string DisabledFolder => Path.Combine(ModsFolder, "Disabled");

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
            mods.AddRange(OffPlugins(mods).ToList());
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Listing the plugins that are switched off failed: {e}");
        }
        try
        {
            var packs = AssetPacks().ToList();
            packsAtStart ??= packs.ToDictionary(PackKey, p => p.On);
            foreach (var pack in packs)
                pack.OnAtStart = packsAtStart.TryGetValue(PackKey(pack), out var on) && on;
            mods.AddRange(packs);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Listing the custom content packs failed: {e}");
        }
        return mods;
    }

    // Just the loaded plugins, without looking at the disk.
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
            var file = info.Location;
            yield return new ModInfo
            {
                Name = meta.Name,
                Version = meta.Version?.ToString(),
                Kind = "Plugin",
                Detail = meta.GUID,
                Guid = meta.GUID,
                File = file,
                // Renamed to ".off" earlier in this session: still loaded, but off at the next start.
                On = string.IsNullOrEmpty(file) || System.IO.File.Exists(file) || !System.IO.File.Exists(file + OffSuffix),
                // Without Mod Manager there would be no way to switch it back on.
                CanSwitch = !string.IsNullOrEmpty(file) && meta.GUID != Plugin.Guid,
                Needs = info.Dependencies
                    .Where(d => (d.Flags & BepInDependency.DependencyFlags.HardDependency) != 0)
                    .Select(d => d.DependencyGUID).ToList(),
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

    // Plugins renamed to ".dll.off" before the game started: BepInEx skipped them, so their name and
    // version are read from the file's BepInPlugin attribute without loading it.
    private static IEnumerable<ModInfo> OffPlugins(List<ModInfo> loaded)
    {
        var known = new HashSet<string>(loaded.Where(m => !string.IsNullOrEmpty(m.File)).Select(m => Path.GetFullPath(m.File)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(Paths.PluginPath, "*.dll" + OffSuffix, AllFiles))
        {
            var file = path.Substring(0, path.Length - OffSuffix.Length);
            if (known.Contains(Path.GetFullPath(file)))
                continue;
            ModInfo mod = null;
            try
            {
                mod = ReadPlugin(path);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Could not read {path}: {e.Message}");
            }
            if (mod == null)
                continue;
            mod.File = file;
            mod.On = false;
            mod.OnAtStart = false;
            mod.CanSwitch = true;
            yield return mod;
        }
    }

    private static ModInfo ReadPlugin(string path)
    {
        using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(path);
        foreach (var type in assembly.MainModule.Types)
        {
            var plugin = type.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "BepInEx.BepInPlugin");
            if (plugin == null || plugin.ConstructorArguments.Count < 3)
                continue;
            var guid = plugin.ConstructorArguments[0].Value as string;
            return new ModInfo
            {
                Name = plugin.ConstructorArguments[1].Value as string ?? Path.GetFileName(path),
                Version = plugin.ConstructorArguments[2].Value as string,
                Kind = "Plugin",
                Detail = guid,
                Guid = guid,
                // A dependency without flags is a hard one.
                Needs = type.CustomAttributes.Where(a => a.AttributeType.FullName == "BepInEx.BepInDependency")
                    .Where(a => a.ConstructorArguments.Count < 2 || (Convert.ToInt32(a.ConstructorArguments[1].Value) & 1) != 0)
                    .Select(a => a.ConstructorArguments[0].Value as string).Where(g => g != null).ToList()
            };
        }
        return null;
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

    private static string PackKey(ModInfo pack) => pack.Folder + "/" + pack.Name;

    // A pack is its json files plus the pictures they name. Everything else in a folder (unknown items,
    // pictures no item names, translations) makes up that folder's "Other custom ..." entry.
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
            var packs = new Dictionary<string, ModInfo>();
            var items = new Dictionary<ModInfo, int>();
            foreach (var (dir, on) in new[] { (Path.Combine(root, folder), true), (Path.Combine(DisabledFolder, folder), false) })
            {
                if (!Directory.Exists(dir))
                    continue;
                var files = Directory.EnumerateFiles(dir, "*", AllFiles).ToList();
                var owner = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files.Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                {
                    var (id, pictures) = ReadItem(file);
                    var known = id == null ? null : KnownPacks.FirstOrDefault(p => p.Terms.All(t => id.Contains(t, StringComparison.OrdinalIgnoreCase)));
                    var pack = Pack(packs, folder, known);
                    owner[file] = pack;
                    items[pack] = items.GetValueOrDefault(pack) + 1;
                    foreach (var picture in pictures)
                    {
                        // Full path, so "sub/x.png" in the json matches the listed "sub\x.png". A name that
                        // isn't a path is skipped; that picture then counts as "Other".
                        try
                        {
                            owner.TryAdd(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file), picture)), pack);
                        }
                        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                        {
                        }
                    }
                }
                foreach (var file in files)
                {
                    if (!owner.TryGetValue(file, out var pack))
                    {
                        pack = Pack(packs, folder, null);
                        // Translations aren't json files; every file in that folder counts.
                        if (folder == "Language")
                            items[pack] = items.GetValueOrDefault(pack) + 1;
                    }
                    pack.Files.Add(file);
                    // A pack with some files in place counts as on; switching moves the rest.
                    if (on)
                        pack.On = true;
                }
            }
            foreach (var pack in packs.Values)
            {
                var count = items.GetValueOrDefault(pack);
                pack.Detail += count > 0 ? Items(count) : pack.Files.Count == 1 ? "1 file" : $"{pack.Files.Count} files";
                yield return pack;
            }
        }
    }

    private static ModInfo Pack(Dictionary<string, ModInfo> packs, string folder, KnownPack known)
    {
        var name = known?.Name ?? $"Other custom {folder.ToLowerInvariant()}";
        if (!packs.TryGetValue(name, out var pack))
            packs[name] = pack = new ModInfo
            {
                Name = name,
                Kind = $"Custom {folder.ToLowerInvariant()}",
                Detail = known != null ? $"by {known.Author}, " : "",
                Folder = folder,
                On = false,
                CanSwitch = true
            };
        return pack;
    }

    private static string Items(int count) => count == 1 ? "1 item" : $"{count} items";

    // The item's id and the picture files it names.
    private static (string Id, List<string> Pictures) ReadItem(string file)
    {
        var pictures = new List<string>();
        try
        {
            var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file), options);
            FindPictures(doc.RootElement, pictures);
            var id = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var value)
                     && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
            return (id, pictures);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Could not read {file}: {e.Message}");
            return (null, pictures);
        }
    }

    private static void FindPictures(JsonElement element, List<string> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    FindPictures(property.Value, found);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    FindPictures(item, found);
                break;
            case JsonValueKind.String:
                var text = element.GetString();
                if (text != null && text.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && text.IndexOfAny(Path.GetInvalidPathChars()) < 0)
                    found.Add(text);
                break;
        }
    }
}
