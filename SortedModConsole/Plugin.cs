using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SortedModConsole
{
    [BepInPlugin(Plugin.modGUID, Plugin.modName, Plugin.modVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string modGUID = "dev.davesco.SortedModConsole";
        public const string modName = "SortedModConsole";
        public const string modVersion = "0.1.0";
        internal static ManualLogSource mlg = BepInEx.Logging.Logger.CreateLogSource(modName);

        internal static ConfigEntry<Key> hotkey;
        internal static ConfigEntry<bool> menuButton;
        internal static ConfigEntry<int> maxLinesPerMod;
        internal static ConfigEntry<int> maxProblemsPerMod;
        internal static ConfigEntry<int> maxTotalLines;
        internal static ConfigEntry<int> fontSize;
        internal static ConfigEntry<float> uiScale;
        internal static ConfigEntry<bool> loadStartupLog;

        void Awake()
        {
            hotkey = Config.Bind("General", "Hotkey", Key.F8, "Opens and closes the console. Esc also closes it.");
            menuButton = Config.Bind("General", "MainMenuButton", true, "Show a button in the main menu that opens the console.");
            maxLinesPerMod = Config.Bind("Limits", "MaxLinesPerMod", 2000,
                "Info and debug lines kept for each mod. Past this a mod's oldest are dropped, other mods keep theirs. Repeats of the same line count as one.");
            maxProblemsPerMod = Config.Bind("Limits", "MaxProblemsPerMod", 1000,
                "Warnings and errors kept for each mod, separately from the info lines, so spam never pushes them out.");
            maxTotalLines = Config.Bind("Limits", "MaxTotalLines", 50000,
                "Limit on all mods together, to cap memory. When reached, lines are dropped from the mod that keeps the most.");
            loadStartupLog = Config.Bind("General", "LoadStartupLog", true,
                "Read what was logged before this mod loaded (other mods' startup) back from LogOutput.log.");
            fontSize = Config.Bind("Display", "FontSize", 14, "Text size at 1080p. Scales with the screen height.");
            uiScale = Config.Bind("Display", "UiScale", 1f, "Extra scale for the whole console, on top of the screen height scaling.");

            // Startup log first, then the listener: whatever is logged from here on arrives live.
            if (loadStartupLog.Value)
                StartupLog.Load();
            BepInEx.Logging.Logger.Listeners.Add(new ConsoleLogListener());

            // Own object, so the console does not depend on BepInEx's manager object surviving.
            var go = new GameObject("SortedModConsole");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<ConsoleWindow>();

            mlg.LogInfo($"Plugin {modName} is loaded! Press {hotkey.Value} to open the console.");
        }
    }
}
