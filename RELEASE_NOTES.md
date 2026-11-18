Mod Manager 1.3.0 for Horses of Hoofprint Bay.

New in this version: each mod's settings now fold under its name. Click the name to open or close them. They start closed when you first open the Mods menu, and the ones you open stay open until you quit the game. Clicking a mod in the left column opens its settings and scrolls to them.

A mod with only one checkbox in its settings no longer gets a "Reset to defaults" button.

For mod authors: `ISettingsPage.Section(heading, content, hint)` adds a heading that folds part of your page. Skip Training uses it for its next version.

Requires BepInEx 6 bleeding edge build 793 (IL2CPP, Windows x64); BepInEx 5 can't load this game. To update, extract the zip into the game folder and replace the old `ModManager.dll`. The README has the full install steps.
