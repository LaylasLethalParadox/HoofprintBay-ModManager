using System;
using System.IO;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace ModManager;

// The settings window is the model for the mod window; its layout is written out when it first opens.
[HarmonyPatch]
internal static class SettingsDump
{
    [HarmonyPatch(typeof(UiSettings), nameof(UiSettings.Start))]
    [HarmonyPostfix]
    private static void AfterStart(UiSettings __instance)
    {
        try
        {
            // Dump the whole window: climb to the object just below the canvas.
            var window = __instance.transform;
            var canvas = __instance.GetComponentInParent<Canvas>();
            while (canvas != null && window.parent != null && window.parent != canvas.rootCanvas.transform)
                window = window.parent;
            UiDump.Write("settings", window, 12);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Dumping the settings window failed: {e}");
        }
    }
}

// Writes the hierarchy of a menu to BepInEx\ModManager-<name>.txt, so the mod window can be built from
// the game's own objects. Only when the file is missing: delete it to get a fresh one.
// A development aid, left out of normal builds: build with -p:UiDump=true to get it.
internal static class UiDump
{
    private static readonly System.Collections.Generic.HashSet<string> Done = new();

    [System.Diagnostics.Conditional("UI_DUMP")]
    public static void Write(string name, Transform start, int maxDepth)
    {
        if (start == null || !Done.Add(name))
            return;
        try
        {
            var path = Path.Combine(BepInEx.Paths.BepInExRootPath, $"ModManager-{name}.txt");
            if (File.Exists(path))
                return;
            var text = new StringBuilder();
            Append(text, start, 0, maxDepth);
            File.WriteAllText(path, text.ToString());
            Plugin.Logger.LogInfo($"Wrote the layout of '{start.name}' to {path}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Writing the {name} layout failed: {e}");
        }
    }

    private static void Append(StringBuilder text, Transform t, int depth, int maxDepth)
    {
        var go = t.gameObject;
        text.Append(' ', depth * 2).Append(go.name).Append(go.activeSelf ? "" : " (inactive)");
        var rect = t.TryCast<RectTransform>();
        if (rect != null)
            text.Append($" pos={rect.anchoredPosition} size={rect.sizeDelta} anchors={rect.anchorMin}-{rect.anchorMax}");
        text.Append(" [");
        foreach (var component in go.GetComponents<Component>())
            if (component != null)
                text.Append(component.GetIl2CppType().Name).Append(' ');
        text.Append(']');
        var label = go.GetComponent<TMP_Text>();
        if (label != null)
            text.Append($" \"{label.text}\"");
        text.AppendLine();

        if (depth >= maxDepth)
            return;
        for (var i = 0; i < t.childCount; i++)
            Append(text, t.GetChild(i), depth + 1, maxDepth);
    }
}
