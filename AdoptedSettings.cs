using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ModManager;

// In a save the game's tooltip box is on a canvas drawn below the mod window's, so it would open behind the
// window. While the window is open the box is moved in front of it. Unity itself calls OnEnable and OnDisable,
// so these patches always run, also when Escape closes the window.
[HarmonyPatch]
internal static class TooltipLayering
{
    private static Transform home;
    private static int homeIndex;

    [HarmonyPatch(typeof(CloseOnEscape), nameof(CloseOnEscape.OnEnable))]
    [HarmonyPostfix]
    private static void AfterEnable(CloseOnEscape __instance)
    {
        var box = TooltipDisplay.i;
        if (box == null || __instance.gameObject.name != ModsPanel.WindowName || box.transform.parent == __instance.transform.parent)
            return;
        home = box.transform.parent;
        homeIndex = box.transform.GetSiblingIndex();
        box.transform.SetParent(__instance.transform.parent, false);
        box.transform.SetAsLastSibling();
    }

    [HarmonyPatch(typeof(CloseOnEscape), nameof(CloseOnEscape.OnDisable))]
    [HarmonyPostfix]
    private static void AfterDisable(CloseOnEscape __instance)
    {
        var box = TooltipDisplay.i;
        if (__instance.gameObject.name != ModsPanel.WindowName || home == null || box == null)
            return;
        box.transform.SetParent(home, false);
        box.transform.SetSiblingIndex(homeIndex);
        home = null;
    }
}

// Other plugins add their settings as rows of the game's settings window. A row is recognised by its
// name starting with the plugin's name ("ImprovedStaff_Slider0"). Rows with a slider, checkbox or
// dropdown are hidden there and shown in the mod window instead (ModsPanel makes stand-in controls
// that drive the hidden ones, so the other plugin's own code still does the saving).
[HarmonyPatch]
internal static class AdoptedSettings
{
    internal enum RowKind { Slider, Toggle, Dropdown, Text, Other }

    private static readonly HashSet<IntPtr> Hidden = new();

    // Plugins build their rows when the settings window is enabled; this runs after them.
    [HarmonyPatch(typeof(UiSettings), nameof(UiSettings.OnEnable))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void AfterEnable(UiSettings __instance)
    {
        try
        {
            var rows = Rows(__instance, ModRegistry.InstalledPlugins()).ToList();
            if (!Plugin.MoveOtherSettings.Value)
            {
                // Only undo our own hiding: a plugin may have hidden a row for its own reasons.
                foreach (var (row, _, _) in rows)
                    if (Hidden.Remove(row.Pointer))
                        row.SetActive(true);
                return;
            }

            // A plugin's heading stays with any of its rows that can't be moved.
            var staying = rows.Where(r => KindOf(r.Row) == RowKind.Other).Select(r => r.Owner).ToHashSet();
            foreach (var (row, owner, _) in rows)
            {
                var kind = KindOf(row);
                if (kind == RowKind.Other || (kind == RowKind.Text && staying.Contains(owner)) || !row.activeSelf)
                    continue;
                row.SetActive(false);
                Hidden.Add(row.Pointer);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Moving other mods' settings failed: {e}");
        }
    }

    // The rows of other plugins in the two columns of a settings window.
    internal static IEnumerable<(GameObject Row, ModInfo Owner, bool PerSave)> Rows(UiSettings ui, List<ModInfo> plugins)
    {
        foreach (var (content, perSave) in Columns(ui))
            for (var i = 0; i < content.childCount; i++)
            {
                var child = content.GetChild(i);
                var owner = ModRegistry.OwnerOf(child.name, plugins);
                if (owner != null)
                    yield return (child.gameObject, owner, perSave);
            }
    }

    private static IEnumerable<(Transform Content, bool PerSave)> Columns(UiSettings ui)
    {
        if (ui.perSavegameContent != null)
            yield return (ui.perSavegameContent, true);
        var global = ModsPanel.Deep(ui.transform, "Scroll View Global");
        var content = global != null ? global.Find("Viewport/Content") : null;
        if (content != null)
            yield return (content, false);
    }

    // The tooltip a plugin keeps for one of its rows. Plugins that show tooltips in the settings window
    // have a list of texts (a string[] with "tip" in its name) in the order of their rows
    // ("ImprovedStaff_Slider0" -> entry 0). Only read, never changed.
    internal static string TooltipFor(ModInfo owner, string rowName)
    {
        try
        {
            var digits = System.Text.RegularExpressions.Regex.Match(rowName, @"(\d+)$");
            if (owner.Assembly == null || !digits.Success)
                return null;
            var index = int.Parse(digits.Value);

            Type[] types;
            try
            {
                types = owner.Assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException e)
            {
                types = Array.FindAll(e.Types, t => t != null);
            }
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            foreach (var type in types)
                foreach (var field in type.GetFields(flags))
                    if (field.FieldType == typeof(string[]) && field.Name.Contains("tip", StringComparison.OrdinalIgnoreCase)
                        && field.GetValue(null) is string[] texts && index < texts.Length)
                        return texts[index];
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Reading the tooltip of '{rowName}' failed: {e.Message}");
        }
        return null;
    }

    // Which control a row is built around. Buttons, text boxes and rows with several controls can't be
    // stood in for; those stay where they are. The toggle and scrollbar inside a dropdown don't count.
    internal static RowKind KindOf(GameObject row)
    {
        var dropdown = row.GetComponentInChildren<TMP_Dropdown>(true);
        var others = row.GetComponentsInChildren<Selectable>(true)
            .Count(s => dropdown == null || !s.transform.IsChildOf(dropdown.transform));
        if (dropdown != null)
            return others == 0 ? RowKind.Dropdown : RowKind.Other;
        if (others == 0)
            return RowKind.Text;
        if (others > 1)
            return RowKind.Other;
        if (row.GetComponentInChildren<Slider>(true) != null)
            return RowKind.Slider;
        return row.GetComponentInChildren<Toggle>(true) != null ? RowKind.Toggle : RowKind.Other;
    }
}
