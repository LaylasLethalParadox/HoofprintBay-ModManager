using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ModManager;

// The mod manager window. It is a copy of the game's settings window (frame, title, close button,
// the two scroll columns) with the game's logic removed; the columns are filled with copies of the
// settings window's own rows, so every control looks and feels like the game's.
//   left column:  the installed mods
//   right column: the settings of every mod that has some (its own page and/or BepInEx config entries)
internal static class ModsPanel
{
    // Rows taken from the settings window; every row of the mod window is a copy of one of these.
    private class Templates
    {
        public GameObject Title, Heading, Note, Hint, Toggle, Slider, Dropdown, Input, ResetButton, Spacer;
    }

    internal const string WindowName = "ModManager window";
    private const float ContentWidth = 640;

    private static GameObject window;
    private static Transform modsColumn, settingsColumn;
    private static ScrollRect modsScroll, settingsScroll;
    private static Templates templates;
    // The game's own settings window (the mod window is a copy of it); other mods' rows live there.
    private static UiSettings source;

    private static List<ModInfo> mods = new();

    public static void Open(Transform canvasRoot)
    {
        try
        {
            if (window == null && !Build(canvasRoot))
                return;
            // Active before filling: layout is skipped on inactive objects. Right above the game's settings
            // window, not on top of everything, so the game's tooltip box stays in front of it.
            window.SetActive(true);
            window.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
            mods = ModRegistry.Collect();
            AdoptRows();
            FillMods();
            FillSettings();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Opening the mod manager failed: {e}");
        }
    }

    private static void Close() => window.SetActive(false);


    // ---- window ----

    private static bool Build(Transform canvasRoot)
    {
        // In a game the settings window may sit in another canvas than the pause menu.
        var original = canvasRoot.GetComponentInChildren<UiSettings>(true);
        if (original == null)
            original = Resources.FindObjectsOfTypeAll<UiSettings>().FirstOrDefault(s => s.transform.parent != null); // not a prefab asset
        if (original == null)
        {
            Plugin.Logger.LogError("The settings window wasn't found, so the mod window can't be built.");
            return false;
        }

        var copy = Object.Instantiate(original.gameObject, original.transform.parent, false).Cast<GameObject>();
        copy.name = WindowName;
        copy.SetActive(false);
        // The game's settings logic must not run on the copy.
        Object.DestroyImmediate(copy.GetComponent<UiSettings>());
        foreach (var name in new[] { "GradientEditor", "UiColorPicker", "WaitForInputPanel" })
        {
            var extra = Deep(copy.transform, name);
            if (extra != null)
                Object.Destroy(extra.gameObject);
        }
        // LanguageUpdater would look our texts up as translation keys and show "Missing: ...".
        foreach (var updater in copy.GetComponentsInChildren<LanguageUpdater>(true))
        {
            updater.enabled = false;
            Object.Destroy(updater);
        }

        var t = new Templates();
        var missing = new List<string>();
        t.Title = Template(copy, "TextSettingTitleDifficulty", missing);
        t.Heading = Template(copy, "TextSettingHeading", missing);
        t.Note = Template(copy, "TextOnlyAvailableInGame", missing);
        t.Hint = Template(copy, "ResolutionExplanation", missing);
        t.Toggle = Template(copy, "ToggleQuestMoney", missing);
        t.Slider = Template(copy, "PrestigeRewardMultiplier", missing);
        t.Dropdown = Template(copy, "Resolution", missing);
        t.Input = Template(copy, "InputFieldTargetFramerate", missing);
        t.ResetButton = Template(copy, "ButtonReset", missing);
        t.Spacer = Template(copy, "Spacer", missing);

        var modsView = Deep(copy.transform, "Scroll View Global");
        var settingsView = Deep(copy.transform, "Scroll View PerGame");
        var title = Deep(copy.transform, "Title");
        var left = Deep(copy.transform, "TextSettingsGlobal");
        var right = Deep(copy.transform, "TextSettingsSaveGame");
        var close = Deep(copy.transform, "ButtonClose");
        if (modsView == null) missing.Add("Scroll View Global");
        if (settingsView == null) missing.Add("Scroll View PerGame");
        if (title == null) missing.Add("Title");
        if (left == null) missing.Add("TextSettingsGlobal");
        if (right == null) missing.Add("TextSettingsSaveGame");
        if (close == null) missing.Add("ButtonClose");
        if (missing.Count > 0)
        {
            Plugin.Logger.LogError($"The settings window doesn't look as expected (missing: {string.Join(", ", missing)}), so the mod window is not built.");
            Object.Destroy(copy);
            return false;
        }

        // Template rows leave the columns and wait, inactive, in a holder inside the window.
        var holder = new GameObject("Templates");
        holder.transform.SetParent(copy.transform, false);
        holder.SetActive(false);
        foreach (var template in new[] { t.Title, t.Heading, t.Note, t.Hint, t.Toggle, t.Slider, t.Dropdown, t.Input, t.ResetButton, t.Spacer })
            template.transform.SetParent(holder.transform, false);
        templates = t;

        modsColumn = modsView.Find("Viewport/Content");
        settingsColumn = settingsView.Find("Viewport/Content");
        modsScroll = modsView.GetComponent<ScrollRect>();
        settingsScroll = settingsView.GetComponent<ScrollRect>();
        Clear(modsColumn);
        Clear(settingsColumn);
        StripExtras(modsColumn);
        StripExtras(settingsColumn);

        title.GetComponent<TMP_Text>().text = "Mods";
        left.GetComponent<TMP_Text>().text = "Installed Mods";
        right.GetComponent<TMP_Text>().text = "Mod Settings";
        var closeButton = close.GetComponent<Button>();
        // A fresh event drops the settings window's close handler that came with the copy.
        closeButton.onClick = new Button.ButtonClickedEvent();
        closeButton.onClick.AddListener(new Action(Close));

        window = copy;
        source = original;
        return true;
    }

    // Other mods can add scripts to the settings columns (hover watchers); the copy only needs its layout.
    private static void StripExtras(Transform column)
    {
        foreach (var behaviour in column.GetComponents<MonoBehaviour>())
            if (behaviour.TryCast<LayoutGroup>() == null && behaviour.TryCast<ContentSizeFitter>() == null)
                Object.DestroyImmediate(behaviour);
    }

    // ---- settings other mods put in the game's settings window ----

    private static void AdoptRows()
    {
        try
        {
            if (source == null || !Plugin.MoveOtherSettings.Value)
                return;
            // Other mods build their rows, and fill them with the loaded save's values, when the settings
            // window is enabled. Enabling and disabling it right away (nothing is drawn in between) has the
            // same effect as opening and closing it, so the rows are there and up to date.
            if (!source.gameObject.activeSelf)
            {
                source.gameObject.SetActive(true);
                source.gameObject.SetActive(false);
            }
            var plugins = mods.Where(m => m.Kind == "Plugin").ToList();
            foreach (var (row, owner, perSave) in AdoptedSettings.Rows(source, plugins))
                if (AdoptedSettings.KindOf(row) is AdoptedSettings.RowKind.Slider or AdoptedSettings.RowKind.Toggle or AdoptedSettings.RowKind.Dropdown)
                    owner.Adopted.Add((row, perSave));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Looking for other mods' settings failed: {e}");
        }
    }

    private static void AddAdopted(ModInfo mod)
    {
        // Per-save settings are locked outside of a save, like in the game's settings window.
        var locked = !source.perSavegameSettings;
        if (locked && mod.Adopted.Any(a => a.PerSave))
            AddText(settingsColumn, templates.Note, Lang.gs("ui.settings.onlyAvailableInSaveGame"));
        foreach (var (row, perSave) in mod.Adopted)
        {
            try
            {
                var lockRow = perSave && locked;
                var tip = AdoptedSettings.TooltipFor(mod, row.name);
                switch (AdoptedSettings.KindOf(row))
                {
                    case AdoptedSettings.RowKind.Slider: StandInSlider(row, lockRow, tip); break;
                    case AdoptedSettings.RowKind.Toggle: StandInToggle(row, lockRow, tip); break;
                    case AdoptedSettings.RowKind.Dropdown: StandInDropdown(row, lockRow, tip); break;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Showing the setting '{row.name}' of {mod.Name} failed: {e}");
            }
        }
    }

    // The label of a row: its first text outside a dropdown, or else the text row just above it.
    private static TMP_Text LabelOf(GameObject row)
    {
        var dropdown = row.GetComponentInChildren<TMP_Dropdown>(true);
        var own = row.GetComponentsInChildren<TMP_Text>(true)
            .FirstOrDefault(t => dropdown == null || !t.transform.IsChildOf(dropdown.transform));
        if (own != null)
            return own;
        var index = row.transform.GetSiblingIndex();
        return index > 0 ? row.transform.parent.GetChild(index - 1).GetComponent<TMP_Text>() : null;
    }

    private static bool warnedNoTooltips;

    // The game's own tooltip (the box that shows e.g. a horse's confidence), on the whole row:
    // pointer events reach the row from its slider, checkbox or dropdown as well as from the label.
    private static void AddTooltip(GameObject row, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (TooltipDisplay.i == null)
        {
            if (!warnedNoTooltips)
                Plugin.Logger.LogWarning("The game's tooltip display isn't in this scene, so there are no tooltips here.");
            warnedNoTooltips = true;
            return;
        }
        // Text doesn't receive the pointer by default.
        var label = row.transform.Find("Label");
        (label != null ? label.GetComponent<TMP_Text>() : row.GetComponent<TMP_Text>()).raycastTarget = true;
        row.AddComponent<Tooltip>().tooltipText = text;
    }

    // Stand-ins read the hidden control and set it when changed, which runs the other mod's own handlers.
    private static void StandInSlider(GameObject row, bool locked, string tip)
    {
        var real = row.GetComponentInChildren<Slider>(true);
        var realLabel = LabelOf(row);
        var copy = Clone(templates.Slider, settingsColumn, "Setting " + row.name);
        var label = copy.transform.Find("Label").GetComponent<TMP_Text>();
        label.text = realLabel != null ? realLabel.text : row.name;
        AddTooltip(copy, tip);

        var slider = copy.GetComponentInChildren<Slider>(true);
        // A fresh event drops the prestige handler that came with the copy.
        slider.onValueChanged = new Slider.SliderEvent();
        slider.minValue = real.minValue;
        slider.maxValue = real.maxValue;
        slider.wholeNumbers = real.wholeNumbers;
        slider.SetValueWithoutNotify(real.value);
        slider.interactable = !locked;
        var marker = copy.transform.Find("MarkerDefault");
        if (marker != null)
            marker.gameObject.SetActive(false);
        slider.onValueChanged.AddListener(new Action<float>(value =>
        {
            real.value = value;
            if (realLabel != null)
                SetLabel(label, realLabel.text);
        }));
    }

    private static void StandInToggle(GameObject row, bool locked, string tip)
    {
        var real = row.GetComponentInChildren<Toggle>(true);
        var realLabel = LabelOf(row);
        var copy = Clone(templates.Toggle, settingsColumn, "Setting " + row.name);
        var label = copy.transform.Find("Label").GetComponent<TMP_Text>();
        label.text = realLabel != null ? realLabel.text : row.name;
        AddTooltip(copy, tip);
        var toggle = copy.GetComponent<Toggle>();
        // A fresh event drops the game's quest money handler that came with the copy.
        toggle.onValueChanged = new Toggle.ToggleEvent();
        toggle.SetIsOnWithoutNotify(real.isOn);
        toggle.interactable = !locked;
        toggle.onValueChanged.AddListener(new Action<bool>(on => real.isOn = on));
    }

    private static void StandInDropdown(GameObject row, bool locked, string tip)
    {
        var real = row.GetComponentInChildren<TMP_Dropdown>(true);
        var realLabel = LabelOf(row);
        var copy = ChoiceRow(realLabel != null ? realLabel.text : row.name);
        AddTooltip(copy, tip);
        var dropdown = copy.GetComponentInChildren<TMP_Dropdown>(true);
        Place(dropdown.transform, new Vector2(10, -5), new Vector2(300, 40));
        // A fresh event drops the resolution handler that came with the copy.
        dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();
        dropdown.ClearOptions();
        var options = new Il2CppSystem.Collections.Generic.List<string>();
        for (var i = 0; i < real.options.Count; i++)
            options.Add(real.options[i].text);
        dropdown.AddOptions(options);
        dropdown.SetValueWithoutNotify(real.value);
        dropdown.RefreshShownValue();
        dropdown.interactable = !locked;
        dropdown.onValueChanged.AddListener(new Action<int>(i => real.value = i));
    }

    private static GameObject Template(GameObject window, string name, List<string> missing)
    {
        var found = Deep(window.transform, name);
        if (found == null)
            missing.Add(name);
        return found != null ? found.gameObject : null;
    }

    internal static Transform Deep(Transform parent, string name)
    {
        for (var i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child.name == name)
                return child;
            var found = Deep(child, name);
            if (found != null)
                return found;
        }
        return null;
    }

    // Destroy only takes effect at the end of the frame, so the old rows are also switched off
    // for the layout.
    private static void Clear(Transform column)
    {
        for (var i = column.childCount - 1; i >= 0; i--)
        {
            var child = column.GetChild(i).gameObject;
            child.SetActive(false);
            Object.Destroy(child);
        }
    }

    private static void Refresh(Transform column, ScrollRect scroll)
    {
        LayoutRebuilder.ForceRebuildLayoutImmediate(column.Cast<RectTransform>());
        scroll.verticalNormalizedPosition = 1f;
    }

    // ---- left column: the installed mods ----

    private static void FillMods(bool keepScroll = false)
    {
        var position = modsScroll.verticalNormalizedPosition;
        Clear(modsColumn);

        AddFolders();

        if (mods.Count == 0)
            AddText(modsColumn, templates.Note, "No mods detected.");

        foreach (var group in mods.GroupBy(m => m.Kind == "Plugin" ? "Plugins" : m.Kind)
                     .OrderBy(g => g.Key == "Plugins" ? 0 : 1).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            AddText(modsColumn, templates.Title, group.Key);
            foreach (var mod in group.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                var heading = string.IsNullOrEmpty(mod.Version) ? mod.Name : $"{mod.Name}  v{mod.Version}";
                var row = AddText(modsColumn, templates.Heading, heading);
                // Clicking a mod that has settings jumps to them in the right column.
                if (mod.HasSettings)
                    MakeClickable(row, () => ScrollToSettings(mod));
                if (mod.CanSwitch)
                    AddSwitch(row, mod);
                AddHint(modsColumn, DetailsOf(mod));
                AddSpacer(modsColumn);
            }
        }
        Refresh(modsColumn, modsScroll);
        FitLabels(modsColumn, modsScroll);
        if (keepScroll)
            modsScroll.verticalNormalizedPosition = position;
    }

    private const float SideControlWidth = 190;

    // Open until clicked shut; only for this game session.
    private static bool foldersOpen = true;

    // Where the mods are on disk, each with a button that opens the folder. Clicking the title folds them
    // away, like the competition headings Skip Training adds to the horse book.
    private static void AddFolders()
    {
        var title = AddText(modsColumn, templates.Title, $"{(foldersOpen ? "▼" : "▶")} Folders");
        MakeClickable(title, () =>
        {
            foldersOpen = !foldersOpen;
            FillMods(keepScroll: true);
        });
        if (foldersOpen)
        {
            AddFolder("Plugins", BepInEx.Paths.PluginPath);
            AddFolder("Tack, clothes, languages", ModRegistry.ModsFolder);
        }
        AddHint(modsColumn, "Mods switched on or off change the next time the game starts.");
    }

    private static void AddFolder(string text, string path)
    {
        var row = AddText(modsColumn, templates.Heading, text);
        var button = Clone(templates.ResetButton, row.transform, "Open folder");
        PlaceRight(button.transform, new Vector2(SideControlWidth - 10, 50));
        button.GetComponentInChildren<TMP_Text>(true).text = "Open folder";
        var click = button.GetComponent<Button>();
        click.onClick = new Button.ButtonClickedEvent();
        click.onClick.AddListener(new Action(() => ModSwitch.OpenFolder(path)));
        KeepClear(row);
        // Narrower than other hints: the button reaches down beside it.
        var hint = AddHint(modsColumn, path.TrimEnd('\\', '/'));
        var rect = Rect(hint.transform);
        rect.sizeDelta = new Vector2(ContentWidth - SideControlWidth, rect.sizeDelta.y);
        AddSpacer(modsColumn);
    }

    // Checkbox with "On"/"Off" at the right end of a mod's name.
    private static void AddSwitch(GameObject heading, ModInfo mod)
    {
        var copy = Clone(templates.Toggle, heading.transform, "Switch " + mod.Name);
        PlaceRight(copy.transform, new Vector2(SideControlWidth - 60, 50));
        var label = copy.transform.Find("Label").GetComponent<TMP_Text>();
        var labelRect = Rect(label.transform);
        labelRect.pivot = new Vector2(0, 0.5f);
        labelRect.anchoredPosition = new Vector2(50, 0);
        labelRect.sizeDelta = new Vector2(SideControlWidth - 110, 45);
        label.text = mod.On ? "On" : "Off";
        var toggle = copy.GetComponent<Toggle>();
        // A fresh event drops the game's quest money handler that came with the copy.
        toggle.onValueChanged = new Toggle.ToggleEvent();
        toggle.SetIsOnWithoutNotify(mod.On);
        toggle.onValueChanged.AddListener(new Action<bool>(on =>
        {
            var problem = ModSwitch.Set(mod, on);
            // One DLL can hold several plugins; renaming it switches all of them.
            if (problem == null && mod.Folder == null)
                foreach (var other in mods.Where(m => m.Folder == null && m.File != null
                             && string.Equals(m.File, mod.File, StringComparison.OrdinalIgnoreCase)))
                    other.On = on;
            // Also when it failed: the checkbox goes back to what is on disk.
            FillMods(keepScroll: true);
            if (problem != null)
                ShowProblem($"{mod.Name} could not be switched {(on ? "on" : "off")}", problem);
        }));
        KeepClear(heading);
    }

    // The game's own info box; the reason is in the log as well, for scenes without one.
    private static void ShowProblem(string heading, string text)
    {
        try
        {
            NotificationManager.i?.ShowInfoDialog(true, heading, text);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Showing the message '{heading}' failed: {e}");
        }
    }

    // Anchored to the right end of its row, which FitLabels stretches to the edge of the column.
    private static void PlaceRight(Transform transform, Vector2 size)
    {
        var rect = Rect(transform);
        rect.anchorMin = new Vector2(1, 0.5f);
        rect.anchorMax = new Vector2(1, 0.5f);
        rect.pivot = new Vector2(1, 0.5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = new Vector2(0, 0);
    }

    // The row's own text stays left of the control at its right end.
    private static void KeepClear(GameObject row)
    {
        var text = row.GetComponent<TMP_Text>();
        text.margin = new Vector4(text.margin.x, text.margin.y, SideControlWidth, text.margin.w);
    }

    // The grey line under a mod's name: its id or pack details, and what a switch will do.
    private static string DetailsOf(ModInfo mod)
    {
        var parts = new List<string> { string.IsNullOrEmpty(mod.Detail) ? mod.Kind : mod.Detail };
        if (mod.On != mod.OnAtStart)
            parts.Add(mod.On ? "on after a restart" : "off after a restart");
        string Names(IEnumerable<ModInfo> list) => string.Join(", ", list.Select(m => m.Name));
        if (!mod.On && mod.Guid != null)
        {
            var needing = mods.Where(m => m.On && m.Needs.Contains(mod.Guid)).ToList();
            if (needing.Count > 0)
                parts.Add($"{Names(needing)} won't load without it");
        }
        if (mod.On)
        {
            var off = mods.Where(m => !m.On && m.Guid != null && mod.Needs.Contains(m.Guid)).ToList();
            if (off.Count > 0)
                parts.Add($"won't load while {Names(off)} is off");
        }
        return string.Join(" - ", parts);
    }

    // The text itself is the button. The game's ClickSound gives it the hover animation and click sound of
    // the game's buttons, and passes the mouse wheel on to the scroll view. It finds its button when it wakes.
    private static void MakeClickable(GameObject row, Action onClick)
    {
        row.SetActive(false);
        // The hover animation scales the row; from its left edge, so that left-aligned text doesn't jump sideways.
        var rect = Rect(row.transform);
        rect.pivot = new Vector2(0, rect.pivot.y);
        var text = row.GetComponent<TMP_Text>();
        text.raycastTarget = true;
        var button = row.AddComponent<Button>();
        button.targetGraphic = text;
        button.onClick.AddListener(onClick);
        row.AddComponent<ClickSound>().theButton = button;
        row.SetActive(true);
    }

    // ---- right column: the settings of every mod that has some ----

    // Where each mod's settings start in the right column (its title row).
    private static readonly Dictionary<ModInfo, RectTransform> settingsStart = new();
    // Empty room after the last mod, so that its title can be scrolled to the top of the column too.
    private static GameObject tail;
    private static RectTransform lastStart;

    private static RectTransform Viewport => settingsScroll.viewport != null ? settingsScroll.viewport : Rect(settingsScroll.transform);

    // Distance from the top of the column's content to the top of a row, in the content's own units.
    private static float OffsetOf(RectTransform row)
    {
        var content = settingsScroll.content;
        return content.rect.yMax - content.InverseTransformPoint(row.TransformPoint(new Vector3(0, row.rect.yMax, 0))).y;
    }

    private static void ScrollToSettings(ModInfo mod)
    {
        if (!settingsStart.TryGetValue(mod, out var start) || start == null)
        {
            Plugin.Logger.LogWarning($"Clicked {mod.Name}, but its settings aren't in the list.");
            return;
        }
        var content = settingsScroll.content;
        LayoutRebuilder.ForceRebuildLayoutImmediate(content);

        var distance = OffsetOf(start);
        var scrollable = content.rect.height - Viewport.rect.height;
        var position = scrollable > 0 ? Mathf.Clamp01(1f - distance / scrollable) : 1f;
        settingsScroll.StopMovement();
        settingsScroll.verticalNormalizedPosition = position;
    }

    // Labels and one-line texts (titles, headings, notes) reach to the edge of the column, and one too long
    // for that shrinks instead of being cut off or running over the next row.
    private static void FitLabels(Transform column, ScrollRect scroll)
    {
        const float margin = 12;
        var viewport = scroll.viewport != null ? scroll.viewport : Rect(scroll.transform);
        for (var i = 0; i < column.childCount; i++)
        {
            var row = column.GetChild(i);
            var found = row.name == "Text" ? row : row.Find("Label");
            var text = found != null ? found.GetComponent<TMP_Text>() : null;
            if (text == null)
                continue;
            var rect = Rect(found);
            var left = viewport.InverseTransformPoint(rect.TransformPoint(new Vector3(rect.rect.xMin, 0, 0))).x;
            var width = viewport.rect.xMax - left - margin;
            // Keeps the left edge in place, wherever the label's pivot is.
            rect.anchoredPosition += new Vector2(rect.pivot.x * (width - rect.sizeDelta.x), 0);
            rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.fontSizeMax = text.fontSize;
            ShrinkToFit(text);
        }
    }

    // TextMeshPro's auto sizing doesn't shrink text that isn't wrapped, so this does. Beyond 60% it would be
    // unreadable, so the end of the text is cut off with "..." and the whole text goes in a tooltip.
    private static void ShrinkToFit(TMP_Text text)
    {
        const float smallest = 0.6f;
        var full = text.fontSizeMax;
        text.fontSize = full;
        // Only when needed: with characters from a fallback font it can cut off text that fits.
        text.overflowMode = TextOverflowModes.Overflow;
        var needed = text.GetPreferredValues(text.text, float.PositiveInfinity, float.PositiveInfinity).x;
        var width = text.rectTransform.rect.width - text.margin.x - text.margin.z;
        if (needed <= width)
            return;
        text.fontSize = full * Mathf.Max(smallest, width / needed);
        if (width / needed >= smallest)
            return;
        text.overflowMode = TextOverflowModes.Ellipsis;
        var row = text.gameObject.name == "Label" ? text.transform.parent.gameObject : text.gameObject;
        if (row.GetComponent<Tooltip>() == null)
            AddTooltip(row, text.text);
    }

    // A label whose text changes with its control, such as "Scritches per Day: 10".
    private static void SetLabel(TMP_Text label, string text)
    {
        label.text = text;
        if (label.textWrappingMode == TextWrappingModes.NoWrap)
            ShrinkToFit(label);
    }

    private static void SetTailHeight(float height)
    {
        var rect = Rect(tail.transform);
        rect.sizeDelta = new Vector2(rect.sizeDelta.x, height);
        var element = tail.GetComponent<LayoutElement>();
        element.minHeight = height;
        element.preferredHeight = height;
    }

    private static void UpdateTail()
    {
        if (tail == null || lastStart == null)
            return;
        var content = settingsScroll.content;
        SetTailHeight(0);
        LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        SetTailHeight(Mathf.Max(0, Viewport.rect.height - (content.rect.height - OffsetOf(lastStart))));
        LayoutRebuilder.ForceRebuildLayoutImmediate(content);
    }

    private static void FillSettings(bool keepScroll = false)
    {
        var position = settingsScroll.verticalNormalizedPosition;
        Clear(settingsColumn);
        settingsStart.Clear();
        tail = null;
        lastStart = null;

        var withSettings = mods.Where(m => m.HasSettings).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (withSettings.Count == 0)
            AddText(settingsColumn, templates.Note, "None of the installed mods has settings.");
        foreach (var mod in withSettings)
            AddModSettings(mod);
        // With one mod there is nothing to jump between.
        if (withSettings.Count > 1)
        {
            tail = AddSpacer(settingsColumn);
            tail.AddComponent<LayoutElement>();
        }
        Refresh(settingsColumn, settingsScroll);
        FitLabels(settingsColumn, settingsScroll);
        UpdateTail();
        if (keepScroll)
            settingsScroll.verticalNormalizedPosition = position;
    }

    private static void AddModSettings(ModInfo mod)
    {
        lastStart = Rect(AddText(settingsColumn, templates.Title, mod.Name).transform);
        settingsStart[mod] = lastStart;

        // Settings the plugin keeps itself (e.g. per save game), then its BepInEx config entries.
        if (mod.Page != null)
        {
            var page = new PageBuilder();
            try
            {
                mod.Page(page);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Showing the settings of {mod.Name} failed: {e}");
            }
            page.RefreshVisibility();
        }

        if (mod.Adopted.Count > 0)
            AddAdopted(mod);

        var entries = mod.Entries;
        foreach (var section in entries.GroupBy(e => e.Definition.Section))
        {
            if (!string.IsNullOrWhiteSpace(section.Key))
                AddText(settingsColumn, templates.Heading, section.Key);
            foreach (var entry in section)
            {
                AddEntry(entry);
                var hint = entry.Description?.Description;
                if (!string.IsNullOrWhiteSpace(hint))
                    AddHint(settingsColumn, hint);
            }
            AddSpacer(settingsColumn);
        }

        // Mod Manager has a single checkbox; a reset button for it would only be clutter.
        if (entries.Count > 0 && mod.Guid != Plugin.Guid)
        {
            var reset = Clone(templates.ResetButton, settingsColumn, "Reset " + mod.Name);
            reset.GetComponentInChildren<TMP_Text>(true).text = "Reset to defaults";
            var resetButton = reset.GetComponent<Button>();
            resetButton.onClick = new Button.ButtonClickedEvent();
            resetButton.onClick.AddListener(new Action(() =>
            {
                foreach (var entry in entries)
                    entry.BoxedValue = entry.DefaultValue;
                FillSettings(keepScroll: true);
            }));
        }
        AddSpacer(settingsColumn);
    }

    // The kind of control follows the kind of setting.
    private static void AddEntry(ConfigEntryBase entry)
    {
        var type = entry.SettingType;
        var allowed = entry.Description?.AcceptableValues;
        // "MoveOtherModsSettings" reads as "Move Other Mods Settings".
        var key = System.Text.RegularExpressions.Regex.Replace(entry.Definition.Key, "(?<=[a-z0-9])(?=[A-Z])", " ");

        if (type == typeof(bool))
            AddToggle(key, entry);
        else if (allowed != null && Property(allowed, "AcceptableValues") is IEnumerable list)
            AddDropdown(key, entry, list.Cast<object>().Select(v => TomlTypeConverter.ConvertToString(v, type)).ToArray());
        // A flags enum can hold several names at once, which a dropdown can't show.
        else if (type.IsEnum && !type.IsDefined(typeof(FlagsAttribute), false))
            AddDropdown(key, entry, Enum.GetNames(type));
        else if (allowed != null && IsNumber(type) && Property(allowed, "MinValue") != null && Property(allowed, "MaxValue") != null)
            AddSlider(key, entry, Convert.ToDouble(Property(allowed, "MinValue")), Convert.ToDouble(Property(allowed, "MaxValue")));
        else
            AddInput(key, entry);
    }

    private static object Property(object target, string name) => target.GetType().GetProperty(name)?.GetValue(target);

    private static bool IsNumber(Type type) => type == typeof(int) || type == typeof(float) || type == typeof(double);

    // Checkbox row.
    private static void AddToggle(string text, ConfigEntryBase entry)
    {
        var row = Clone(templates.Toggle, settingsColumn, "Setting " + text);
        row.transform.Find("Label").GetComponent<TMP_Text>().text = text;
        var toggle = row.GetComponent<Toggle>();
        // A fresh event drops the game's quest money handler that came with the copy.
        toggle.onValueChanged = new Toggle.ToggleEvent();
        toggle.SetIsOnWithoutNotify((bool)entry.BoxedValue);
        toggle.onValueChanged.AddListener(new Action<bool>(on => entry.BoxedValue = on));
    }

    // Dropdown row, laid out like the SkipTraining mod lays out the game's dropdown row:
    // control on the left, label on the right.
    private static void AddDropdown(string text, ConfigEntryBase entry, string[] choices)
    {
        var row = ChoiceRow(text);
        var dropdown = row.GetComponentInChildren<TMP_Dropdown>(true);
        Place(dropdown.transform, new Vector2(10, -5), new Vector2(300, 40));
        // A fresh event drops the resolution handler that came with the copy.
        dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();
        dropdown.ClearOptions();
        var options = new Il2CppSystem.Collections.Generic.List<string>();
        foreach (var choice in choices)
            options.Add(choice);
        dropdown.AddOptions(options);
        dropdown.SetValueWithoutNotify(Math.Max(0, Array.IndexOf(choices, entry.GetSerializedValue())));
        dropdown.RefreshShownValue();
        dropdown.onValueChanged.AddListener(new Action<int>(i => entry.SetSerializedValue(choices[i])));
    }

    // Text box row, for text and for numbers without a range. A value the setting can't take
    // is put back to the current one.
    private static void AddInput(string text, ConfigEntryBase entry)
    {
        var input = InputRow(text);
        input.characterValidation = entry.SettingType == typeof(int) ? TMP_InputField.CharacterValidation.Integer
            : entry.SettingType == typeof(float) || entry.SettingType == typeof(double) ? TMP_InputField.CharacterValidation.Decimal
            : TMP_InputField.CharacterValidation.None;
        input.SetTextWithoutNotify(entry.GetSerializedValue());
        input.onEndEdit.AddListener(new Action<string>(value =>
        {
            try
            {
                entry.SetSerializedValue(value);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"'{value}' is not a valid value for {entry.Definition.Key}: {e.Message}");
            }
            input.SetTextWithoutNotify(entry.GetSerializedValue());
        }));
    }

    private static TMP_InputField InputRow(string text) => InputRow(text, out _);

    private static TMP_InputField InputRow(string text, out GameObject row)
    {
        row = ChoiceRow(text);
        var dropdown = row.GetComponentInChildren<TMP_Dropdown>(true);
        Object.Destroy(dropdown.gameObject);

        var box = Clone(templates.Input, row.transform, "Input");
        Place(box.transform, new Vector2(10, -5), new Vector2(300, 40));
        var rect = Rect(box.transform);
        rect.anchorMin = new Vector2(0, 1);
        rect.anchorMax = new Vector2(0, 1);
        rect.pivot = new Vector2(0, 1);
        // The copy is the colour picker's hex box: drop its "#" and "hex..." hint.
        foreach (var name in new[] { "Text", "Text Area/Placeholder" })
        {
            var decoration = box.transform.Find(name);
            if (decoration != null)
                decoration.gameObject.SetActive(false);
        }

        var input = box.GetComponent<TMP_InputField>();
        // Fresh events drop the handlers that came with the copy.
        input.onEndEdit = new TMP_InputField.SubmitEvent();
        input.onSubmit = new TMP_InputField.SubmitEvent();
        input.onValueChanged = new TMP_InputField.OnChangeEvent();
        input.characterLimit = 0;
        input.lineType = TMP_InputField.LineType.SingleLine;
        return input;
    }

    // Slider row with the game's default marker, for numbers that have a range.
    private static void AddSlider(string text, ConfigEntryBase entry, double min, double max)
    {
        var row = Clone(templates.Slider, settingsColumn, "Setting " + text);
        var label = row.transform.Find("Label").GetComponent<TMP_Text>();

        var whole = entry.SettingType == typeof(int);
        var slider = row.GetComponentInChildren<Slider>(true);
        // A fresh event drops the prestige handler that came with the copy.
        slider.onValueChanged = new Slider.SliderEvent();
        slider.minValue = (float)min;
        slider.maxValue = (float)max;
        slider.wholeNumbers = whole;

        void ShowLabel(double value) => SetLabel(label, $"{text}: {(whole ? value.ToString("0") : value.ToString("0.##"))}");

        slider.SetValueWithoutNotify((float)Convert.ToDouble(entry.BoxedValue));
        ShowLabel(Convert.ToDouble(entry.BoxedValue));

        if (entry.DefaultValue != null && max > min)
            MarkDefault(row, slider, (Convert.ToDouble(entry.DefaultValue) - min) / (max - min));

        slider.onValueChanged.AddListener(new Action<float>(value =>
        {
            entry.BoxedValue = Convert.ChangeType(value, entry.SettingType);
            ShowLabel(value);
        }));
    }

    // Marks the default like the game marks the defaults of its sliders (fraction 0..1 of the track).
    private static void MarkDefault(GameObject row, Slider slider, double fraction)
    {
        var marker = row.transform.Find("MarkerDefault");
        if (marker == null)
            return;
        var sliderRect = Rect(slider.transform);
        var handleRange = sliderRect.sizeDelta.x - 20f;
        var x = sliderRect.anchoredPosition.x + 10f + handleRange * (float)Math.Clamp(fraction, 0, 1);
        Rect(marker).anchoredPosition = new Vector2(x, Rect(marker).anchoredPosition.y);
    }

    // Settings a plugin draws itself through ISettingsPage, with the same rows as the config entries.
    private class PageBuilder : ISettingsPage
    {
        private readonly List<(GameObject Row, Func<bool> Visible)> conditional = new();

        public bool Locked { get; set; }

        public void RefreshVisibility()
        {
            foreach (var (row, visible) in conditional)
            {
                try
                {
                    row.SetActive(visible());
                }
                catch (Exception e)
                {
                    // One mod's faulty condition must not take the other mods' settings down with it.
                    Plugin.Logger.LogError($"A 'visible' condition of a mod's settings failed: {e}");
                }
            }
        }

        private void Track(GameObject row, Func<bool> visible)
        {
            if (visible != null)
                conditional.Add((row, visible));
        }

        public void Title(string text, Func<bool> visible = null) => Track(AddText(settingsColumn, templates.Title, text), visible);
        public void Heading(string text, Func<bool> visible = null) => Track(AddText(settingsColumn, templates.Heading, text), visible);
        public void Note(string text, Func<bool> visible = null) => Track(AddText(settingsColumn, templates.Note, text), visible);
        public void Hint(string text, Func<bool> visible = null) => Track(AddHint(settingsColumn, text), visible);
        public void Spacer() => AddSpacer(settingsColumn);

        public void Toggle(string label, Func<bool> get, Action<bool> set, Func<bool> visible = null)
        {
            var row = Clone(templates.Toggle, settingsColumn, "Setting " + label);
            row.transform.Find("Label").GetComponent<TMP_Text>().text = label;
            var toggle = row.GetComponent<UnityEngine.UI.Toggle>();
            // A fresh event drops the game's quest money handler that came with the copy.
            toggle.onValueChanged = new UnityEngine.UI.Toggle.ToggleEvent();
            toggle.SetIsOnWithoutNotify(get());
            toggle.interactable = !Locked;
            toggle.onValueChanged.AddListener(new Action<bool>(on =>
            {
                set(on);
                RefreshVisibility();
                UpdateTail();
            }));
            Track(row, visible);
        }

        public void Choice(string label, string[] choices, Func<int> get, Action<int> set, Func<bool> visible = null)
        {
            var row = ChoiceRow(label);
            var dropdown = row.GetComponentInChildren<TMP_Dropdown>(true);
            Place(dropdown.transform, new Vector2(10, -5), new Vector2(300, 40));
            // A fresh event drops the resolution handler that came with the copy.
            dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();
            dropdown.ClearOptions();
            var options = new Il2CppSystem.Collections.Generic.List<string>();
            foreach (var choice in choices)
                options.Add(choice);
            dropdown.AddOptions(options);
            dropdown.SetValueWithoutNotify(get());
            dropdown.RefreshShownValue();
            dropdown.interactable = !Locked;
            dropdown.onValueChanged.AddListener(new Action<int>(i =>
            {
                set(i);
                RefreshVisibility();
                UpdateTail();
            }));
            Track(row, visible);
        }

        public void Slider(Func<int, string> label, int min, int max, Func<int> get, Action<int> set, int? defaultValue = null, Func<bool> visible = null)
        {
            var row = Clone(templates.Slider, settingsColumn, "Setting slider");
            var text = row.transform.Find("Label").GetComponent<TMP_Text>();

            var slider = row.GetComponentInChildren<UnityEngine.UI.Slider>(true);
            // A fresh event drops the prestige handler that came with the copy.
            slider.onValueChanged = new UnityEngine.UI.Slider.SliderEvent();
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = true;
            slider.SetValueWithoutNotify(get());
            // The slider's value, as it clamps one outside its range.
            text.text = label((int)Math.Round(slider.value));
            slider.interactable = !Locked;
            if (defaultValue.HasValue && max > min)
                MarkDefault(row, slider, (defaultValue.Value - min) / (double)(max - min));

            // No visibility refresh here: it would rebuild nothing a slider can change, and
            // switching rows while the slider is dragged is not wanted.
            slider.onValueChanged.AddListener(new Action<float>(value =>
            {
                var v = (int)Math.Round(value);
                set(v);
                SetLabel(text, label(v));
            }));
            Track(row, visible);
        }

        public void Number(string label, int min, int max, Func<int> get, Action<int> set, Func<bool> visible = null)
        {
            var input = InputRow(label, out var row);
            input.characterValidation = TMP_InputField.CharacterValidation.Integer;
            input.SetTextWithoutNotify(get().ToString(System.Globalization.CultureInfo.InvariantCulture));
            input.interactable = !Locked;
            input.onEndEdit.AddListener(new Action<string>(value =>
            {
                if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    set(Math.Clamp(v, min, max));
                input.SetTextWithoutNotify(get().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }));
            Track(row, visible);
        }
    }

    // Row copied from the resolution setting, laid out like the slider rows: control on the left,
    // label on the right.
    private static GameObject ChoiceRow(string text)
    {
        var row = Clone(templates.Dropdown, settingsColumn, "Setting " + text);
        Rect(row.transform).sizeDelta = new Vector2(665, 50);
        var label = row.transform.Find("Label");
        Place(label, new Vector2(330, 0), new Vector2(330, 50));
        label.GetComponent<TMP_Text>().text = text;
        return row;
    }

    // ---- rows ----

    private static GameObject AddText(Transform column, GameObject template, string text)
    {
        var row = Clone(template, column, "Text");
        row.GetComponent<TMP_Text>().text = text;
        return row;
    }

    // Small explanation text, copied from the resolution setting's explanation.
    private static GameObject AddHint(Transform column, string text)
    {
        var row = Clone(templates.Hint, column, "Hint");
        Rect(row.transform).sizeDelta = new Vector2(ContentWidth, Rect(row.transform).sizeDelta.y);
        row.GetComponent<TMP_Text>().text = text;
        return row;
    }

    private static GameObject AddSpacer(Transform column) => Clone(templates.Spacer, column, "Spacer");

    private static GameObject Clone(GameObject template, Transform parent, string name)
    {
        var row = Object.Instantiate(template, parent, false).Cast<GameObject>();
        row.name = name;
        row.SetActive(true);
        foreach (var updater in row.GetComponentsInChildren<LanguageUpdater>(true))
        {
            updater.enabled = false;
            Object.Destroy(updater);
        }
        return row;
    }

    private static RectTransform Rect(Transform transform) => transform.Cast<RectTransform>();

    private static void Place(Transform transform, Vector2 position, Vector2 size)
    {
        var rect = Rect(transform);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
    }
}
