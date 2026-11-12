using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ModManager;

// Switches mods on and off by moving their files where the game and BepInEx don't look, so nothing else
// has to be stored and taking the files back by hand works too. Both only read their folders at start,
// so a switch takes effect the next time the game starts.
//   plugin: its DLL is renamed to "<name>.dll.off" (Windows allows renaming a loaded DLL)
//   pack:   its files move between Saves~/Mods/<Folder> and Saves~/Mods/Disabled/<Folder>
internal static class ModSwitch
{
    // Null when the files are now where the new state needs them, else why not, in words for the player.
    public static string Set(ModInfo mod, bool on)
    {
        string problem;
        try
        {
            problem = mod.Folder != null ? MovePack(mod, on) : RenamePlugin(mod, on);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Switching {mod.Name} {(on ? "on" : "off")} failed: {e}");
            problem = e.Message;
        }
        if (problem == null)
        {
            mod.On = on;
            Plugin.Logger.LogInfo($"{mod.Name} is switched {(on ? "on" : "off")}; this takes effect the next time the game starts.");
        }
        else
            Plugin.Logger.LogWarning($"{mod.Name} could not be switched {(on ? "on" : "off")}: {problem}");
        return problem;
    }

    private static string RenamePlugin(ModInfo mod, bool on)
    {
        var off = mod.File + ModRegistry.OffSuffix;
        var (from, to) = on ? (off, mod.File) : (mod.File, off);
        if (!File.Exists(from))
            return File.Exists(to) ? null : $"{Path.GetFileName(mod.File)} is no longer in the plugins folder.";
        if (File.Exists(to))
            return $"Both {Path.GetFileName(mod.File)} and {Path.GetFileName(off)} are in {Path.GetDirectoryName(mod.File)}. Remove the one you don't want.";
        File.Move(from, to);
        return null;
    }

    private static string MovePack(ModInfo mod, bool on)
    {
        var active = Path.Combine(ModRegistry.ModsFolder, mod.Folder);
        var disabled = Path.Combine(ModRegistry.DisabledFolder, mod.Folder);
        var (fromRoot, toRoot) = on ? (disabled, active) : (active, disabled);
        string firstProblem = null;
        // One file at a time, each on its own: if one can't move (in use, no permission), the list still says
        // where every file is, so switching again picks up where this stopped.
        var moved = mod.Files.Select(file =>
        {
            if (!file.StartsWith(fromRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return file;
            var target = Path.Combine(toRoot, Path.GetRelativePath(fromRoot, file));
            if (File.Exists(target))
            {
                // Never overwrite: the other copy may be a newer version of the file.
                firstProblem ??= $"{target} already exists.";
                return file;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Move(file, target);
                return target;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Could not move {file} to {target}: {e.Message}");
                firstProblem ??= e.Message;
                return file;
            }
        }).ToList();
        mod.Files = moved;
        var left = moved.Count(f => f.StartsWith(fromRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        return left == 0 ? null : $"{(left == 1 ? "1 file" : $"{left} files")} could not be moved. {firstProblem}";
    }

    public static void OpenFolder(string path)
    {
        try
        {
            // A trailing backslash would escape the closing quote of the argument.
            path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Directory.CreateDirectory(path);
            // Full path: a bare "explorer.exe" is searched for in the game's folder first.
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var process = Process.Start(new ProcessStartInfo(explorer, $"\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Opening {path} failed: {e}");
        }
    }
}
