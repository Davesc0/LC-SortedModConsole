using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace SortedModConsole
{
    /// <summary>
    /// While the console is open: a free cursor, no clicks going through to the game's menus
    /// underneath, and no player controls (so typing in the search box does not walk or emote).
    /// Everything it turned off is turned back on when the console closes.
    /// </summary>
    internal sealed class InputBlocker
    {
        private readonly List<EventSystem> eventSystems = new List<EventSystem>();
        private readonly List<InputActionMap> actionMaps = new List<InputActionMap>();
        private CursorLockMode cursorLock;
        private bool cursorVisible;
        private bool engaged;

        public void Engage()
        {
            if (engaged)
                return;
            engaged = true;
            cursorLock = Cursor.lockState;
            cursorVisible = Cursor.visible;
            Tick();
        }

        /// <summary>Run every frame while open: catches things the game turns on again, or that
        /// appear later (a scene change brings a new EventSystem, joining a lobby a new player).</summary>
        public void Tick()
        {
            if (!engaged)
                return;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // EventSystem.current moves on to the next enabled one once the current is disabled.
            for (int i = 0; i < 8 && EventSystem.current != null; i++)
            {
                var es = EventSystem.current;
                es.enabled = false;
                eventSystems.Add(es);
            }

            try
            {
                DisableMaps(IngamePlayerSettings.Instance?.playerInput?.actions);
                PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
                if (player != null)
                    DisableMaps(player.playerActions?.asset);
            }
            catch
            {
                // Game objects half torn down during a scene change; next frame tries again.
            }
        }

        public void Release()
        {
            if (!engaged)
                return;
            engaged = false;

            foreach (var es in eventSystems)
                if (es != null)
                    es.enabled = true;
            eventSystems.Clear();

            foreach (var map in actionMaps)
            {
                try { map.Enable(); } catch { }
            }
            actionMaps.Clear();

            Cursor.lockState = cursorLock;
            Cursor.visible = cursorVisible;
        }

        private void DisableMaps(InputActionAsset asset)
        {
            if (asset == null)
                return;
            foreach (var map in asset.actionMaps)
            {
                if (!map.enabled)
                    continue;
                map.Disable();
                actionMaps.Add(map);
            }
        }
    }
}
