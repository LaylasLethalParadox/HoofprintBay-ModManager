using System;
using System.Collections.Generic;

namespace ModManager;

// What a plugin can draw on its page of the mod window, besides its BepInEx config entries. For
// settings a plugin keeps itself (per save game, in its own files).
// Rows can have a visible condition; it is checked again after every change.
public interface ISettingsPage
{
    // Locked controls are shown but can't be changed (the game locks per-save settings outside a save).
    bool Locked { get; set; }

    void Title(string text, Func<bool> visible = null);
    void Heading(string text, Func<bool> visible = null);
    void Note(string text, Func<bool> visible = null);
    void Hint(string text, Func<bool> visible = null);
    void Spacer();

    void Toggle(string label, Func<bool> get, Action<bool> set, Func<bool> visible = null);
    void Choice(string label, string[] choices, Func<int> get, Action<int> set, Func<bool> visible = null);

    // Whole-number slider from min to max; the label is built from the current value.
    // defaultValue puts the game's default marker on the slider.
    void Slider(Func<int, string> label, int min, int max, Func<int> get, Action<int> set, int? defaultValue = null, Func<bool> visible = null);

    // Text box for a whole number from min to max (since 1.2.0). A typed value outside the range is
    // clamped; anything that isn't a number puts the box back to get().
    void Number(string label, int min, int max, Func<int> get, Action<int> set, Func<bool> visible = null);
}

public static class ModSettings
{
    internal record Registration(Action<ISettingsPage> Build, bool ShowConfigEntries);

    private static readonly Dictionary<string, Registration> Pages = new();

    // Call from a plugin's Load. The build action runs every time the plugin's page is shown, so it
    // can read the current values (for example of the loaded save game). The plugin's BepInEx config
    // entries are listed after the page unless showConfigEntries is false (they stay in the .cfg file).
    public static void Register(string pluginGuid, Action<ISettingsPage> build, bool showConfigEntries = true) =>
        Pages[pluginGuid] = new Registration(build, showConfigEntries);

    internal static Registration Find(string pluginGuid) => Pages.GetValueOrDefault(pluginGuid);
}
