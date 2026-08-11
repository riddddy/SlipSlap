using System.Collections.Generic;
using UnityEngine;
using GorillaLocomotion;

namespace slipslap
{
    // Reskins the red super-slippery walls to look like normal slip walls.
    // Copies the material's appearance only - the name stays the same, and
    // the name is what GetSlidePercentage matches on, so slide values don't
    // change.
    public static class WallSkin
    {
        public static string NormalMaterialName = "rockwall2";

        // Matched against material name or its texture name. Empty = pick the
        // highest slidePercent in materialData.
        public static string RedMaterialName = "rockwall3";

        // Lists every material and slidePercent in the map. Turn on if the
        // wall names ever change.
        public static bool DumpOnStart = false;

        static bool dumped;
        static bool applied;

        // Matched materials + a copy of how each looked before.
        static readonly List<KeyValuePair<Material, Material>> patched =
            new List<KeyValuePair<Material, Material>>();

        public static void Tick()
        {
            if (DumpOnStart && !dumped) Dump();
            if (!applied) Apply();
        }

        // Unity appends " (Instance)" to runtime material copies.
        static string Clean(string name) => name.Replace(" (Instance)", "").Trim();

        static bool NameMatches(string actual, string wanted) =>
            !string.IsNullOrEmpty(wanted) &&
            Clean(actual).Equals(Clean(wanted), System.StringComparison.OrdinalIgnoreCase);

        // Reading mainTexture on a shader without _MainTex spams a Unity warning.
        static Texture? MainTex(Material m) =>
            (m != null && m.HasProperty("_MainTex")) ? m.mainTexture : null;

        static string Describe(Material m)
        {
            var tex = MainTex(m);
            string col = m.HasProperty("_Color") ? m.color.ToString() : "-";
            return $"shader={m.shader.name} tex={(tex != null ? tex.name : "<none>")} color={col}";
        }

        static string FindRedMaterialName(Player player)
        {
            if (!string.IsNullOrEmpty(RedMaterialName)) return RedMaterialName;
            if (player.materialData == null) return "";

            string best = "";
            float bestSlide = float.MinValue;

            // MaterialData is a struct, so no null checks.
            foreach (var md in player.materialData)
            {
                if (string.IsNullOrEmpty(md.matName)) continue;
                if (!md.overrideSlidePercent) continue;
                if (md.slidePercent <= bestSlide) continue;

                bestSlide = md.slidePercent;
                best = md.matName;
            }

            return best;
        }

        static void Apply()
        {
            var player = Player.Instance;
            if (player == null) return;

            string redName = FindRedMaterialName(player);
            if (string.IsNullOrEmpty(redName)) return;

            Material? source = null;
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null && NameMatches(m.name, NormalMaterialName)) { source = m; break; }
                if (source != null) break;
            }

            if (source == null)
            {
                Debug.LogWarning($"[SlipSlap] Material '{NormalMaterialName}' not in this map yet - will retry.");
                return;
            }

            // The same wall can show up as several material instances.
            var found = new List<Material>();
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m == source || found.Contains(m)) continue;

                    var tex = MainTex(m);
                    if (NameMatches(m.name, redName) ||
                        (tex != null && NameMatches(tex.name, redName)))
                        found.Add(m);
                }
            }

            if (found.Count == 0)
            {
                Debug.LogWarning($"[SlipSlap] Nothing matches '{redName}' by material or texture name - will retry.");
                return;
            }

            foreach (var m in found)
            {
                string before = Describe(m);

                patched.Add(new KeyValuePair<Material, Material>(m, new Material(m)));
                m.CopyPropertiesFromMaterial(source);

                Debug.Log($"[SlipSlap]   '{m.name}' (slidePercent {SlideOf(player, m.name)})\n" +
                            $"      was: {before}\n" +
                            $"      now: {Describe(m)}");
            }

            applied = true;
            Debug.Log($"[SlipSlap] Reskinned {found.Count} material(s) matching '{redName}' from '{NormalMaterialName}'.");
        }

        static string SlideOf(Player player, string matName)
        {
            if (player.materialData == null) return "?";
            foreach (var md in player.materialData)
                if (NameMatches(md.matName, matName))
                    return md.slidePercent.ToString("0.###");
            return "?";
        }

        public static void Restore()
        {
            if (!applied) return;

            foreach (var pair in patched)
            {
                if (pair.Key == null || pair.Value == null) continue;
                pair.Key.CopyPropertiesFromMaterial(pair.Value);
                Object.Destroy(pair.Value);
            }

            patched.Clear();
            applied = false;
            Debug.Log("[SlipSlap] Wall textures restored.");
        }

        static void Dump()
        {
            var player = Player.Instance;
            if (player == null || player.materialData == null || player.materialData.Count == 0) return;

            dumped = true;
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("materialData (index | matName | overrides? | slidePercent):");
            for (int i = 0; i < player.materialData.Count; i++)
            {
                var md = player.materialData[i];
                sb.AppendLine($"  {i,2} | {md.matName} | {md.overrideSlidePercent} | {md.slidePercent}");
            }

            var seen = new HashSet<string>();
            int mask = player.locomotionEnabledLayers.value;
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (r == null) continue;
                if ((mask & (1 << r.gameObject.layer)) == 0) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    var t = MainTex(m);
                    seen.Add($"{Clean(m.name)}  (texture: {(t != null ? Clean(t.name) : "<none>")})");
                }
            }

            sb.AppendLine($"materials on climbable geometry ({seen.Count}):");
            foreach (var n in seen) sb.AppendLine("  " + n);

            Debug.Log($"[SlipSlap] {sb}");
        }
    }
}
