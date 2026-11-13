# Mod Manager for Horses of Hoofprint Bay

Adds a Mods menu to [Horses of Hoofprint Bay](https://store.steampowered.com/app/3727090/). You open it from a new sign on the main menu or a Mods button in the pause menu, and it uses the game's own window and controls.

The left side lists your BepInEx plugins and the custom tack, clothes and language files in the game's mods folder. The right side has the settings of every mod that has some. Click a mod's name to jump to its settings.

If another mod adds settings to the game's Settings window, Mod Manager can move them to the Mods menu. The other mod still saves its own values. To leave them where they were, untick **Move Other Mods Settings**.

## Switching mods on and off

Every mod has an On/Off checkbox, and a switch takes effect the next time the game starts. Mod Manager stores nothing; the state is where the files are:

- A plugin is renamed from `Name.dll` to `Name.dll.off`, so BepInEx skips it.
- A pack's json files and the pictures they use move to `Saves~\Mods\Disabled\Tack` (or `Clothes`, `Language`) and move back when you switch it on. Existing files are never overwritten.

To undo a switch by hand, rename or move the files back. A save where a horse wears tack from a pack that is off still loads, and the horse is shown without that item.

The Folders section at the top of the list has Open folder buttons for the plugins folder and the game's mods folder.

## Install

You need BepInEx 6 bleeding edge build 793 for IL2CPP (Windows x64). BepInEx 5 and the older 6.0.0-pre builds can't load this game.

1. Download `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793+dbc82f0.zip` from [builds.bepinex.dev](https://builds.bepinex.dev/projects/bepinex_be) and extract it next to `Horses of Hoofprint Bay.exe`.
2. Start the game once and close it.
3. Open `BepInEx\config\BepInEx.cfg`, find `UnityBaseLibrariesSource =` and delete everything after the `=`. If the folders `BepInEx\interop` and `BepInEx\unity-libs` exist, delete them.
4. Extract the Mod Manager zip into the game folder, so that you have `BepInEx\plugins\ModManager\ModManager.dll`.
5. Start the game. The first start takes a minute or two while BepInEx rebuilds its files.

To uninstall, delete `BepInEx\plugins\ModManager`.

## For mod authors

Your mod shows up in the list without any work. What you need to do for its settings depends on where they live.

### BepInEx config entries

Nothing. A `bool` becomes a checkbox, an enum or `AcceptableValueList` a dropdown, an `AcceptableValueRange` a slider, and anything else a text box.

### Rows in the game's Settings window

Give each row a name that starts with your plugin's name, assembly name or the last part of its GUID, then a non-letter and more text, such as `MyPlugin_Slider0`. A row holding one slider, checkbox or dropdown moves to the Mods menu, and Mod Manager drives your original control so your own handlers still run. Any other row stays in the game's window.

For hover text, keep a static `string[]` with "tip" in its name, in the order of the numbers at the end of your row names.

### Settings your mod stores itself

Draw them in your section with `ModSettings.Register`:

```csharp
[BepInDependency("hoofprintbay.modmanager")]
public class Plugin : BasePlugin
{
    public override void Load()
    {
        ModManager.ModSettings.Register("my.plugin.guid", page =>
        {
            page.Heading("My settings");
            page.Toggle("Do the thing", () => enabled, on => enabled = on);
        });
    }
}
```

`ISettingsPage` has Title, Heading, Note, Hint, Spacer, Toggle, Choice, Slider and Number (a box for a whole number, since 1.2.0; a plugin that uses it should depend on `hoofprintbay.modmanager` version `>=1.2.0`). Each row except Spacer takes a `visible` condition. Pass `showConfigEntries: false` to `Register` to keep your config entries out of the window.

## Custom content names

Custom content is listed by the item ids in its json files. Packs known by name are in `KnownPacks` in `ModRegistry.cs`, and anything else appears as "Other custom tack", "Other custom clothes" or "Other custom language". Pull requests adding packs are welcome.

## Building

You need the .NET 6 SDK and the game with BepInEx installed and started at least once, because the project compiles against files BepInEx generates. This repository contains no game files.

```
dotnet build -c Release -p:GameDir="C:\Path\To\HorsesOfHoofprintBay\\"
```

The build copies the DLL into the game's `BepInEx\plugins\ModManager` folder, so close the game first. `package.ps1` makes the release zip. Adding `-p:UiDump=true` writes the object hierarchy of the game's menus to `BepInEx\ModManager-*.txt`.

## License

GPL-3.0, see [LICENSE](LICENSE).

This is an unofficial fan project and isn't affiliated with or endorsed by the developers of Horses of Hoofprint Bay.
