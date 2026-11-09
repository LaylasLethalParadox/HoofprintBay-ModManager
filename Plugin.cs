using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace ModManager;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "hoofprintbay.modmanager";
    public const string Name = "Mod Manager";
    public const string Version = "1.0.0";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> MoveOtherSettings;

    public override void Load()
    {
        Logger = Log;
        MoveOtherSettings = Config.Bind("Settings", "MoveOtherModsSettings", true,
            "Show the settings other mods add to the game's settings menu in the Mods menu instead, and hide them in the settings menu.");
        var harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(MainMenuButton));
#if UI_DUMP
        harmony.PatchAll(typeof(SettingsDump));
#endif
        harmony.PatchAll(typeof(AdoptedSettings));
        harmony.PatchAll(typeof(TooltipLayering));
        Log.LogInfo("A Mods sign is added below Quit on the main menu; it lists the installed mods and their settings.");
    }
}
