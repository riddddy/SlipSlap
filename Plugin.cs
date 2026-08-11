using Project.Modding;
using UnityEngine;

namespace slipslap
{
    // Old slippery-wall physics, launch included. No GUI.
    [GameModInfo(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
    public class Plugin : BaseGameMod
    {
        const float TickInterval = 3f;
        float next;

        void OnEnable()
        {
            HarmonyPatches.Apply();
            Debug.Log("[SlipSlap] Enabled.");
        }

        void OnDisable()
        {
            WallSkin.Restore();
            HarmonyPatches.Remove();
            Debug.Log("[SlipSlap] Disabled.");
        }

        // Needs the player and map loaded, and has to survive map changes.
        void Update()
        {
            if (Time.time < next) return;
            next = Time.time + TickInterval;

            try { WallSkin.Tick(); }
            catch (System.Exception e) { Debug.LogWarning($"[SlipSlap] WallSkin failed: {e.Message}"); }
        }
    }
}
