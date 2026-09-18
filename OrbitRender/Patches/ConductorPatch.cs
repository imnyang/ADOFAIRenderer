using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using OrbitRender.Renderer;

namespace OrbitRender.Patches
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

    [HarmonyPatch(typeof(AsyncInputUtils), "GetSongPosition")]
    internal static class AsyncSongPositionPatch
    {
        // The synchronous autoplay path normally falls back to
        // AudioSource.time when AsyncInput is inactive. AudioSource.time is
        // driven by the real audio device, so it lags behind the deterministic
        // render clock whenever offline generation is faster than realtime.
        // Keep angle/autoplay calculations on the same timeline as the
        // conductor and the camera events. The target tick is intentionally
        // ignored here: the renderer disables AsyncInput and owns the frame
        // clock for the whole run.
        static bool Prefix(scrConductor __instance, ref double __result)
        {
            if (!RendererController.ControlsTime) return true;
            if (__instance == null || __instance.song == null)
            {
                __result = 0.0;
                return false;
            }

            __result = RendererController.Instance.Clock.SongPosition(
                __instance.dspTimeSong,
                __instance.song.pitch,
                __instance.addoffset,
                0.0);
            return false;
        }
    }

    [HarmonyPatch(typeof(AudioManager), "FindOrLoadAudioClipExternal")]
    internal static class ExternalSongStreamingPatch
    {
        // DownloadHandlerAudioClip reports only the currently buffered portion
        // when streamAudio is enabled. That is fine for normal gameplay, but
        // Unity's offline AudioRenderer cannot advance that stream reliably:
        // volume-driven tiles see silence and the exported WAV contains only a
        // short prefix. Decode external OGG/WAV audio into a complete clip for
        // deterministic renders; leave normal gameplay untouched.
        static void Prefix(ref bool stream)
        {
            if (RendererController.ControlsTime) stream = false;
        }
    }

    [HarmonyPatch(typeof(scrConductor), "StartMusic")]
    internal static class StartMusicPatch
    {
        static bool Prefix(scrConductor __instance, Action onSongScheduled)
        {
            if (!RendererController.ControlsTime) return true;
            // The render owns song scheduling so loading latency cannot advance
            // camera/DOTween state before output frame zero.
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
            // Let Start_Rewind finish its reset, but do not wait on the real
            // audio device: every additional preparation frame would move the
            // camera before frame zero.
            yield return null;
            if (!RendererController.ControlsTime) yield break;
            try
            {
                renderer.ScheduleAudio(conductor);
                scheduled?.Invoke();
                renderer.MusicScheduled();
            }
            catch (Exception ex) { renderer.AbortWithError(ex); yield break; }
            while (RendererController.ControlsTime && renderer.Clock.DspTime < conductor.dspTimeSong)
                yield return null;
            if (RendererController.ControlsTime) conductor.hasSongStarted = true;
        }
    }

    [HarmonyPatch(typeof(scrConductor), "PlayHitTimes")]
    internal static class BgaHitSoundPatch
    {
        // BGA renders keep the actual song audio but omit the gameplay hit
        // sound schedule. This also prevents hold/midspin hit sounds created
        // by the same scheduling pass from entering the captured mix.
        static bool Prefix() => !RendererController.BgaModeActive;
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
            var fps = Math.Max(1, RendererController.Instance.Clock.Fps);
            AccessTools.Field(typeof(scrConductor), "lastReportedPlayheadPosition")
                .SetValue(__instance, __instance.dspTime - 1.0 / fps);
        }
    }

    [HarmonyPatch(typeof(scrConductor), "set_songposition_minusi")]
    internal static class SongPositionPatch
    {
        static void Prefix(scrConductor __instance, ref double value)
        {
            if (!RendererController.ControlsTime || __instance.song == null) return;
            // Avoid the float conversion in the stock Update, retaining double
            // precision throughout long renders. Input calibration compensates
            // live input and must not shift an export, but addoffset is the
            // level-authored song offset and must remain in the chart clock.
            value = RendererController.Instance.Clock.SongPosition(__instance.dspTimeSong,
                __instance.song.pitch, __instance.addoffset, 0.0);
        }
    }
}
