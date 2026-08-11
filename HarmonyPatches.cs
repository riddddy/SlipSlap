using System.Reflection;
using HarmonyLib;
using UnityEngine;
using GorillaLocomotion;

namespace slipslap
{
    // Prefix returns false so Player.LateUpdate never runs and the ported
    // locomotion takes over.
    //
    // Target LateUpdate, not FixedUpdate. The old build ran locomotion in
    // Update() and had a one-line FixedUpdate; this build has it all in
    // LateUpdate. FixedUpdate still resolves by name, so patching it looks
    // like it worked and does nothing.
    public static class HarmonyPatches
    {
        static Harmony? harmony;
        public static bool Applied { get; private set; }

        public static void Apply()
        {
            if (Applied) return;

            var target = AccessTools.Method(typeof(Player), "LateUpdate");
            if (target == null)
            {
                Debug.LogError("[SlipSlap] Couldn't find Player.LateUpdate - nothing patched.");
                return;
            }

            harmony = new Harmony(PluginInfo.GUID);
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(HarmonyPatches).GetMethod(
                    nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic)));

            // GorillaTagger drives haptics and the slide sound off
            // IsHandSliding - one tap on contact, then a continuous buzz for
            // as long as it's true. It expects this build's slide test
            // (slip > iceThreshold), but the port uses the old one
            // (slip > defaultSlideFactor), so normal slip walls buzzed like
            // ice. Physics wants the loose test, feedback wants the strict one.
            var sliding = AccessTools.Method(typeof(Player), "IsHandSliding", new[] { typeof(bool) });
            if (sliding != null)
            {
                harmony.Patch(sliding,
                    postfix: new HarmonyMethod(typeof(HarmonyPatches).GetMethod(
                        nameof(SlidingPost), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            else Debug.LogWarning("[SlipSlap] IsHandSliding not found - slip walls will buzz continuously.");

            Applied = true;
            Debug.Log($"[SlipSlap] Replaced {target.DeclaringType?.Name}.{target.Name} with the ported locomotion.");
        }

        public static void Remove()
        {
            if (!Applied || harmony == null) return;
            harmony.UnpatchAll(PluginInfo.GUID);
            harmony = null;
            Applied = false;
            LegacyLocomotion.Reset();
            Debug.Log("[SlipSlap] Unpatched - stock locomotion restored.");
        }

        // Physics reads the slide flags directly, so this only affects
        // whatever asks Player whether it's sliding.
        static void SlidingPost(Player __instance, bool forLeftHand, ref bool __result)
        {
            if (!__result) return;

            if (!LegacyLocomotion.ContinuousSlideFeedback) { __result = false; return; }
            if (!LegacyLocomotion.StrictSlidingFeedback) return;

            float slip = forLeftHand
                ? __instance.leftHandSlipPercentage
                : __instance.rightHandSlipPercentage;

            if (slip < __instance.iceThreshold) __result = false;
        }

        static bool Pre(Player __instance)
        {
            try { return LegacyLocomotion.Run(__instance); }
            catch (System.Exception e)
            {
                // Fall back to stock for this frame rather than stranding the player.
                Debug.LogError($"[SlipSlap] Ported locomotion threw, running stock this frame: {e}");
                return true;
            }
        }
    }
}
