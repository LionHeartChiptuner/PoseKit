using System;
using System.Reflection;
using BepInEx.Logging;
using FistVR;
using HarmonyLib;
using JerryGloves;
using UnityEngine;

namespace GT3PoseKit
{
    // GlovesTan3 spawns the gloves from its wrist menu button ("Tan Gloves?") in SpawnGloves.FixedUpdate. If that
    // button is pressed while OtherLoader is still loading mods, GlovesTan3 asks Anvil for its "gloves" bundle at
    // the same moment OtherLoader is loading it. OtherLoader's load then fails ("An element with the same key
    // already exists") and the gloves can't be spawned until the game restarts. This holds the spawn until
    // OtherLoader has finished and the GlovesTan3 item exists; the gloves then spawn on their own.
    [HarmonyPatch(typeof(SpawnGloves), "FixedUpdate")]
    static class SpawnGuardPatch
    {
        public static ManualLogSource Log;
        static readonly FieldInfo GlovesField = AccessTools.Field(typeof(SpawnGloves), "Gloves");
        static readonly PropertyInfo ActiveLoaders =
            AccessTools.Property(AccessTools.TypeByName("OtherLoader.LoaderStatus"), "NumActiveLoaders");
        const float MaxWait = 180f;   // give up holding after this long, in case OtherLoader never reports idle
        static float _waitStart = -1f;
        static bool _told;

        static bool Prefix(SpawnGloves __instance)
        {
            if (!SpawnGloves.SpawnTheGloves) { _waitStart = -1f; _told = false; return true; }
            if (GlovesField != null && (GlovesField.GetValue(__instance) as GameObject) != null) return true;

            string why = Waiting();
            if (why == null) { _waitStart = -1f; return true; }

            if (_waitStart < 0f) _waitStart = Time.realtimeSinceStartup;
            bool spawnerMissing = why == "item";
            if (!spawnerMissing && Time.realtimeSinceStartup - _waitStart > MaxWait) return true;
            if (!_told && Log != null)
            {
                _told = true;
                Log.LogMessage(spawnerMissing
                    ? "Gloves will spawn once GlovesTan3's item has loaded."
                    : "Gloves will spawn once OtherLoader finishes loading mods.");
            }
            return false;
        }

        // null when it's safe to spawn, otherwise what we're waiting for
        static string Waiting()
        {
            try
            {
                if (ActiveLoaders != null && Convert.ToInt32(ActiveLoaders.GetValue(null, null)) > 0) return "loader";
            }
            catch { }
            var id = IM.GetSpawnerID("GlovesTan3");
            if (id == null || id.MainObject == null) return "item";
            return null;
        }
    }
}
