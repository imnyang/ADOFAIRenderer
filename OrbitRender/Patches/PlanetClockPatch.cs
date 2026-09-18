using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using OrbitRender.Renderer;

namespace OrbitRender.Patches
{
    // PlanetRenderer's expo pulse uses wall-clock unscaledTime. During a
    // render the game can run faster than the output timeline, which
    // makes that pulse and the planet's visual state advance at the wrong
    // speed. Keep it on the same deterministic clock as the song.
    [HarmonyPatch(typeof(PlanetRenderer), "LateUpdate")]
    internal static class PlanetRendererClockPatch
    {
        private static float UnscaledTime()
        {
            return RendererController.ControlsTime
                ? (float)RendererController.Instance.Clock.Time
                : Time.unscaledTime;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getter = AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledTime));
            var replacement = AccessTools.Method(typeof(PlanetRendererClockPatch), nameof(UnscaledTime));
            var count = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(getter))
                {
                    instruction.operand = replacement;
                    count++;
                }
                yield return instruction;
            }
            if (count == 0)
                throw new InvalidOperationException("Unsupported PlanetRenderer.LateUpdate: unscaled clock was not found.");
        }
    }

    // Keep the planet's non-tween state transitions at the output frame rate
    // as well. Time.captureFramerate normally does this, but the game can
    // replace capture timing while custom levels or filters are loading.
    [HarmonyPatch(typeof(scrPlanet), "Update")]
    internal static class PlanetUpdateClockPatch
    {
        private static float DeltaTime()
        {
            return RendererController.ControlsTime
                ? 1f / RendererController.Instance.Clock.Fps
                : Time.deltaTime;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getter = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            var replacement = AccessTools.Method(typeof(PlanetUpdateClockPatch), nameof(DeltaTime));
            var count = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(getter))
                {
                    instruction.operand = replacement;
                    count++;
                }
                yield return instruction;
            }
            if (count == 0)
                throw new InvalidOperationException("Unsupported scrPlanet.Update: delta clock was not found.");
        }
    }
}
