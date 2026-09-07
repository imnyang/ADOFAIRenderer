using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using ADOFAIRenderer.Renderer;

namespace ADOFAIRenderer.Patches
{
    // Keep the game's beat propagation and deltaSongPos calculation. Replace only
    // the DSP source, including its stall fallback, in the verified Update method.
    [HarmonyPatch]
    internal static class ConductorPatch
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(scrConductor), "Update");
            yield return AccessTools.Method(typeof(scrCountdown), "Update");
        }
        static double DspTime() => RendererController.ControlsTime ? RendererController.Instance.Clock.DspTime : AudioSettings.dspTime;
        static double UnscaledTime() => RendererController.ControlsTime ? RendererController.Instance.Clock.Time : Time.unscaledTimeAsDouble;
        static float UnscaledDelta() => RendererController.ControlsTime ? 1f / RendererController.Instance.Clock.Fps : Time.unscaledDeltaTime;
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var dsp = AccessTools.PropertyGetter(typeof(AudioSettings), nameof(AudioSettings.dspTime));
            var time = AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledTimeAsDouble));
            var delta = AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledDeltaTime));
            int replacements = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(dsp)) { instruction.operand = AccessTools.Method(typeof(ConductorPatch), nameof(DspTime)); replacements++; }
                else if (instruction.Calls(time)) instruction.operand = AccessTools.Method(typeof(ConductorPatch), nameof(UnscaledTime));
                else if (instruction.Calls(delta)) instruction.operand = AccessTools.Method(typeof(ConductorPatch), nameof(UnscaledDelta));
                yield return instruction;
            }
            if (replacements == 0) throw new InvalidOperationException("Unsupported scrConductor.Update: DSP clock was not found.");
        }
    }

    [HarmonyPatch(typeof(scrConductor), "StartMusic")]
    internal static class StartMusicPatch
    {
        static bool Prefix(scrConductor __instance, Action onSongScheduled)
        {
            if (!RendererController.ControlsTime) return true;
            // The ordinary coroutine waits for AudioSource.isPlaying and invokes
            // PostSong according to wall time. Silent rendering owns this lifetime.
            var field = AccessTools.Field(typeof(scrConductor), "startMusicCoroutine");
            var old = field.GetValue(__instance) as Coroutine;
            if (old != null) __instance.StopCoroutine(old);
            __instance.dspTime = RendererController.Instance.Clock.DspTime;
            __instance.dspTimeSong = __instance.dspTime + 1.0;
            field.SetValue(__instance, __instance.StartCoroutine(Schedule(__instance, onSongScheduled)));
            return false;
        }

        static IEnumerator Schedule(scrConductor conductor, Action scheduled)
        {
            var renderer = RendererController.Instance;
            // Allow Start_Rewind to finish its reset before notifying the controller.
            yield return null;
            if (!RendererController.ControlsTime) yield break;
            // Use a fixed one-second preroll instead of the audio-device buffer.
            // Autoplay is enabled after preparation, so keep normal countdown timing.
            try { renderer.ScheduleAudio(conductor); scheduled?.Invoke(); renderer.MusicScheduled(); }
            catch (Exception ex) { renderer.AbortWithError(ex); yield break; }
            while (RendererController.ControlsTime && renderer.Clock.DspTime < conductor.dspTimeSong) yield return null;
            if (RendererController.ControlsTime) conductor.hasSongStarted = true;
        }
    }

    [HarmonyPatch]
    internal static class ConductorResetPatch
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(scrConductor), "Start");
            yield return AccessTools.Method(typeof(scrConductor), "Rewind");
        }
        static void Postfix(scrConductor __instance)
        {
            if (!RendererController.ControlsTime) return;
            __instance.dspTime = RendererController.Instance.Clock.DspTime;
            __instance.dspTimeSong = __instance.dspTime + 1.0;
            AccessTools.Field(typeof(scrConductor), "lastReportedPlayheadPosition").SetValue(__instance, __instance.dspTime - 1.0 / 60.0);
        }
    }

    [HarmonyPatch(typeof(scrConductor), "set_songposition_minusi")]
    internal static class SongPositionPatch
    {
        static void Prefix(scrConductor __instance, ref double value)
        {
            if (!RendererController.ControlsTime || __instance.song == null) return;
            // Avoid the float conversion in the stock Update, retaining double
            // precision throughout long renders. minusv still applies game calibration.
            value = RendererController.Instance.Clock.SongPosition(__instance.dspTimeSong,
                __instance.song.pitch, __instance.addoffset, scrConductor.calibration_i);
        }
    }
}
