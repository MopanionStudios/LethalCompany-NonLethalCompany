using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

namespace NonLethalCompany
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            int ok = 0, failed = 0;

            // Patch each group on its own, so one broken patch is logged and skipped
            // instead of taking every other patch down with it.
            var groups = typeof(Plugin)
                .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Where(t => t.IsDefined(typeof(HarmonyPatch), false));

            foreach (Type group in groups)
            {
                try
                {
                    harmony.CreateClassProcessor(group).Patch();
                    ok++;
                    Log.LogInfo($"Patched: {group.Name}");
                }
                catch (Exception e)
                {
                    failed++;
                    Log.LogError($"FAILED: {group.Name} -> {e.GetType().Name}: {e.Message}");
                }
            }

            Log.LogInfo($"NonLethalCompany loaded: {ok} patches OK, {failed} failed.");
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        internal static void Silence(ParticleSystem ps)
        {
            if (ps == null) return;
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.maxParticles = 0;
        }

        // Clean copies of prefabs that carry blood particles/audio, cached so we only build each once.
        private static readonly Dictionary<GameObject, GameObject> CleanCopies = new();

        internal static GameObject CleanCopy(GameObject source)
        {
            if (source == null) return source;
            if (CleanCopies.ContainsValue(source)) return source; // already one of ours
            if (CleanCopies.TryGetValue(source, out GameObject existing) && existing != null) return existing;

            GameObject copy = UnityEngine.Object.Instantiate(source);
            foreach (ParticleSystem ps in copy.GetComponentsInChildren<ParticleSystem>(true))
                Silence(ps);
            foreach (AudioSource audio in copy.GetComponentsInChildren<AudioSource>(true))
                audio.enabled = false;

            UnityEngine.Object.DontDestroyOnLoad(copy);
            copy.transform.position += Vector3.down * 10000f; // park it far away, it's only used as a template

            CleanCopies[source] = copy;
            return copy;
        }

        // ---------------------------------------------------------------
        // DECALS: these replace the old IL transpilers with plain prefixes.
        // Returning false skips the original method entirely, and doesn't
        // depend on the game's internal code layout.
        // ---------------------------------------------------------------

        [HarmonyPatch(typeof(PlayerControllerB), "DropBlood")]
        private static class NoBloodOnGround
        {
            private static bool Prefix() => false;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "AddBloodToBody")]
        private static class NoBloodOnBody
        {
            private static bool Prefix() => false;
        }

        [HarmonyPatch(typeof(DeadBodyInfo), "MakeCorpseBloody")]
        private static class NoBloodOnCorpse
        {
            private static bool Prefix() => false;
        }

        // Forces deathAnimation to 0 (no decapitation etc.).
        // NOTE: "deathAnimation" must match the parameter name in v85's SpawnDeadBody.
        [HarmonyPatch(typeof(PlayerControllerB), "SpawnDeadBody")]
        private static class NoSpecialDeathAnimation
        {
            private static void Prefix(ref int deathAnimation) => deathAnimation = 0;
        }

        // Forest giant's bloody face decal. The game re-enables it while eating a player,
        // so keep switching it off.
        [HarmonyPatch(typeof(ForestGiantAI), "Update")]
        private static class NoGiantFaceBlood
        {
            private static readonly FieldInfo Face = AccessTools.Field(typeof(ForestGiantAI), "bloodOnFaceDecal");

            private static void Postfix(ForestGiantAI __instance)
            {
                if (Face?.GetValue(__instance) is Behaviour decal && decal.enabled)
                    decal.enabled = false;
            }
        }

        // ---------------------------------------------------------------
        // PARTICLES / ANIMATIONS (carried over from the original, no IL matching)
        // ---------------------------------------------------------------

        [HarmonyPatch(typeof(CaveDwellerAI), "Start")]
        private static class ManEaterParticles
        {
            private static void Postfix(CaveDwellerAI __instance)
            {
                Silence(__instance.killPlayerParticle1);
                Silence(__instance.killPlayerParticle2);
            }
        }

        [HarmonyPatch(typeof(ButlerEnemyAI), "Start")]
        private static class ButlerParticlesStart
        {
            private static void Postfix(ButlerEnemyAI __instance)
            {
                Silence(__instance.stabBloodParticle);
                Silence(__instance.popParticle);
            }
        }

        [HarmonyPatch(typeof(ButlerEnemyAI), "StabPlayerClientRpc")]
        private static class ButlerStab
        {
            private static void Postfix(ButlerEnemyAI __instance) => Silence(__instance.stabBloodParticle);
        }

        [HarmonyPatch(typeof(ButlerEnemyAI), "KillEnemy")]
        private static class ButlerPop
        {
            private static void Postfix(ButlerEnemyAI __instance) => Silence(__instance.popParticle);
        }

        [HarmonyPatch(typeof(KnifeItem), "ItemActivate")]
        private static class KnifeParticles
        {
            private static void Postfix(KnifeItem __instance) => Silence(__instance.bloodParticle);
        }

        [HarmonyPatch(typeof(SpikeRoofTrap), "Start")]
        private static class SpikeTrapPrefab
        {
            private static void Postfix(SpikeRoofTrap __instance) =>
                __instance.deadBodyStickingPointPrefab = CleanCopy(__instance.deadBodyStickingPointPrefab);
        }

        [HarmonyPatch(typeof(KillLocalPlayer), "KillPlayer")]
        private static class FanDeath
        {
            private static void Prefix(KillLocalPlayer __instance)
            {
                if (__instance.causeOfDeath != CauseOfDeath.Fan) return;
                __instance.deathAnimation = 0;
                __instance.spawnPrefab = CleanCopy(__instance.spawnPrefab);
            }
        }

        [HarmonyPatch(typeof(ForestGiantAI), "Start")]
        private static class ForestGiantEatAnimation
        {
            private static void Postfix(ForestGiantAI __instance)
            {
                AnimationClip clip = __instance.creatureAnimator.runtimeAnimatorController.animationClips
                    .FirstOrDefault(c => c.name.Equals("FGiantEatPlayer"));
                if (clip == null) return;
                clip.events = clip.events.Where(e => !e.functionName.Equals("PlayParticle")).ToArray();
            }
        }
    }
}