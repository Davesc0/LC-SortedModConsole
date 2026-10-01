# Sorted Mod Console

I mostly vibe-coded this because I wanted a better way to pick out the debug lines of the mod I
was working on.
Handy for modders while debugging, or for players trying to find out which mod is causing problems.

It's an in-game log console that sorts every log line into a tab for the mod that wrote it, so
one spammy mod doesn't bury everything else.

- **Sorted by mod automatically**, based on BepInEx. Other mods don't need to support it.
- **Problems tab** with only the warnings and errors from every mod. Mods with errors show red.
- **Doesn't jump to the bottom** while you scroll up to read.
- **Spam stays in its own tab.** Each mod keeps its own history, and warnings and errors are
  never pushed out by info spam.
- **Filter and search** by level and text. Click a line for the full text and copy it.

Open it with **F8**, or with the button in the main menu. Settings (hotkey, text size, limits)
can be changed with LethalConfig.

![Example](https://raw.githubusercontent.com/Davesc0/LC-SortedModConsole/main/screenshot.webp)

## Notes

- If a mod logs through plain Unity logging instead of its BepInEx logger, the console can't
  tell which mod it came from. Those lines end up in the "Unity Log" tab.
- Client side only, nobody else needs it.
