using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ModManager;

// Adds a "Mods" button that opens the mod window, in the main menu and in the pause menu
// (Escape in a game). The button is a copy of one of the menu's own buttons, so it looks like them.
[HarmonyPatch]
internal static class MainMenuButton
{
    private const string ButtonName = "ModManager Mods button";
    private const float SignNudgeX = -24;

    [HarmonyPatch(typeof(UiMenu), nameof(UiMenu.Start))]
    [HarmonyPostfix]
    private static void AfterStart(UiMenu __instance)
    {
        try
        {
            // Only the main menu can start a new game; the other UiMenu is the pause menu.
            if (__instance.uiNewGameDialog != null)
                AddToMainMenu(__instance);
            else
                AddToPauseMenu(__instance);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Adding the Mods button failed: {e}");
        }
    }

    private static Transform CanvasRoot(UiMenu menu)
    {
        var canvas = menu.GetComponentInParent<Canvas>();
        return canvas != null ? canvas.rootCanvas.transform : menu.transform;
    }

    // ---- main menu: a sign on the pole ----

    private static void AddToMainMenu(UiMenu menu)
    {
        var root = CanvasRoot(menu);
        UiDump.Write("main-menu", root, 6);

        // The Mods sign is a copy of the Horse Creator sign (it points the same way as the pole's
        // left-hand signs); Settings and Quit only give the spacing between the signs.
        var buttons = menu.GetComponentsInChildren<Button>(true);
        var quit = buttons.FirstOrDefault(b => Matches(b, "quit"));
        var settings = buttons.FirstOrDefault(b => Matches(b, "setting"));
        var creator = buttons.FirstOrDefault(b => Matches(b, "creator"));
        if (quit == null || settings == null || creator == null)
        {
            Plugin.Logger.LogWarning($"The main menu doesn't look as expected (Quit sign found: {quit != null}, Settings sign found: {settings != null}, Horse Creator sign found: {creator != null}), so the Mods sign is not added.");
            LogButtons(buttons);
            return;
        }

        var mods = CopyButton(creator, root);
        // One sign step further down the pole (Settings -> Quit -> Mods), on the Horse Creator's side of it.
        var step = quit.transform.position - settings.transform.position;
        mods.transform.position = new Vector3(creator.transform.position.x, quit.transform.position.y + step.y, creator.transform.position.z);
        // The pole leans, so this far down the sign needs a nudge for its nail to land on it.
        mods.transform.localPosition += new Vector3(SignNudgeX, 0, 0);
        // Drawn last, so it hangs in front of the Support the Devs pole (a "Post" object drawn after Quit).
        mods.transform.SetAsLastSibling();
        Plugin.Logger.LogInfo("Added the Mods sign to the main menu.");
    }

    // ---- pause menu: a button under Settings ----

    private static void AddToPauseMenu(UiMenu menu)
    {
        var root = CanvasRoot(menu);
        UiDump.Write("pause-menu", menu.transform, 5);

        var buttons = menu.GetComponentsInChildren<Button>(true);
        var settings = buttons.FirstOrDefault(b => Matches(b, "setting"));
        if (settings == null)
        {
            Plugin.Logger.LogWarning("No Settings button in the pause menu, so the Mods button is not added there.");
            LogButtons(buttons);
            return;
        }

        var mods = CopyButton(settings, root);
        PlaceBelow(settings, mods.GetComponent<Button>());
        Plugin.Logger.LogInfo("Added the Mods button to the pause menu.");
    }

    // Puts the copy right under the original: a layout group does that by itself, otherwise the
    // buttons below move down by one step to make room.
    private static void PlaceBelow(Button anchor, Button copy)
    {
        var parent = anchor.transform.parent;
        copy.transform.SetSiblingIndex(anchor.transform.GetSiblingIndex() + 1);
        if (parent.GetComponent<LayoutGroup>() != null)
            return;

        var siblings = new List<Button>();
        for (var i = 0; i < parent.childCount; i++)
        {
            var button = parent.GetChild(i).GetComponent<Button>();
            if (button != null && button != copy && button.gameObject.activeSelf)
                siblings.Add(button);
        }
        var y = anchor.transform.position.y;
        var next = siblings.Where(b => b.transform.position.y < y - 1f).OrderByDescending(b => b.transform.position.y).FirstOrDefault();
        var previous = siblings.Where(b => b.transform.position.y > y + 1f).OrderBy(b => b.transform.position.y).FirstOrDefault();
        var height = Math.Abs(anchor.GetComponent<RectTransform>().rect.height * anchor.transform.lossyScale.y);
        var step = next != null ? y - next.transform.position.y
            : previous != null ? previous.transform.position.y - y
            : height * 1.1f;

        foreach (var below in siblings.Where(b => b.transform.position.y < y - 1f))
            below.transform.position -= new Vector3(0, step, 0);
        copy.transform.position = anchor.transform.position - new Vector3(0, step, 0);
    }

    // ---- shared ----

    // A copy of an existing button that opens the mod window instead.
    private static GameObject CopyButton(Button template, Transform canvasRoot)
    {
        var copy = Object.Instantiate(template.gameObject, template.transform.parent, false).Cast<GameObject>();
        copy.name = ButtonName;
        copy.SetActive(true);
        // LanguageUpdater would look "Mods" up as a translation key and show "Missing: ...".
        foreach (var updater in copy.GetComponentsInChildren<LanguageUpdater>(true))
        {
            updater.enabled = false;
            Object.Destroy(updater);
        }
        var label = copy.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
            label.text = "Mods";

        var button = copy.GetComponent<Button>();
        // A fresh event drops the handler that came with the copy.
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(new Action(() => ModsPanel.Open(canvasRoot)));
        return copy;
    }

    private static void LogButtons(IEnumerable<Button> buttons)
    {
        Plugin.Logger.LogWarning("Buttons seen:");
        foreach (var button in buttons)
            Plugin.Logger.LogWarning($"  {button.gameObject.name} \"{TextOf(button)}\"");
    }

    private static bool Matches(Button button, string word) =>
        button.gameObject.name.Contains(word, StringComparison.OrdinalIgnoreCase)
        || TextOf(button).Contains(word, StringComparison.OrdinalIgnoreCase);

    private static string TextOf(Button button)
    {
        var text = button.GetComponentInChildren<TMP_Text>(true);
        return text != null ? text.text ?? "" : "";
    }
}
