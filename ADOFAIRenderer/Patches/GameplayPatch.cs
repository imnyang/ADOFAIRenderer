using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ADOFAIRenderer.Renderer;

namespace ADOFAIRenderer.Patches
{
    [HarmonyPatch(typeof(scrHitTextManager), nameof(scrHitTextManager.ShowHitText), typeof(HitMargin), typeof(scrPlanet), typeof(float))]
    internal static class HideJudgmentsPatch
    {
        static bool Prefix() => !RendererController.ControlsTime;
    }

    [HarmonyPatch(typeof(scnLevelSelect), "CheckAudioBreak")]
    internal static class AudioDevicePatch
    {
        static bool Prefix() => !RendererController.ControlsTime;
    }
    // Select the game's synchronous autoplay path, never synthesize input ticks.
    [HarmonyPatch(typeof(AsyncInputManager), "get_isActive")]
    internal static class SynchronousGameplayPatch
    {
        static bool Prefix(ref bool __result)
        {
            if (!RendererController.ControlsTime) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(scrController), "UpdateInput")]
    internal static class GameplayInputPatch
    {
        static bool Prefix() => !RendererController.ControlsTime;
    }

    // The editor and a few menu components read these directly instead of
    // going through scrController.UpdateInput. Return an empty input state so
    // keyboard, controller, and back/menu actions cannot modify the level
    // while the render clock owns the frame.
    [HarmonyPatch(typeof(RDInput), "GetMain")]
    internal static class RendererMainInputPatch
    {
        static bool Prefix(ref int __result)
        {
            if (!RendererController.ControlsTime) return true;
            __result = 0;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class RendererMainKeyListPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(RDInput), "GetMainPressKeys");
            yield return AccessTools.Method(typeof(RDInput), "GetMainHeldKeys");
        }

        static bool Prefix(ref List<AnyKeyCode> __result)
        {
            if (!RendererController.ControlsTime) return true;
            __result = new List<AnyKeyCode>();
            return false;
        }
    }

    [HarmonyPatch]
    internal static class RendererBackInputPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(RDInput), "backPress");
            yield return AccessTools.PropertyGetter(typeof(RDInput), "backIsPressed");
        }

        static bool Prefix(ref bool __result)
        {
            if (!RendererController.ControlsTime) return true;
            __result = false;
            return false;
        }
    }

    // AsyncInput is used by editor shortcuts and some UI objects without
    // passing through RDInput. Block all query overloads during rendering.
    [HarmonyPatch]
    internal static class RendererAsyncInputPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var method in typeof(AsyncInput).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                if (method.Name == "GetKey" || method.Name == "GetKeyDown" || method.Name == "GetKeyUp")
                    yield return method;
        }

        static bool Prefix(ref bool __result)
        {
            if (!RendererController.ControlsTime) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class RendererInputDevicePatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in new[] {
                typeof(RDInputType_Keyboard), typeof(RDInputType_AsyncKeyboard),
                typeof(RDInputType_Joystick), typeof(RDInputType_Mouse) })
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                    if (method.ReturnType == typeof(bool) && (method.Name == "CheckKeyState" || method.Name == "Back"))
                        yield return method;
            }
        }

        static bool Prefix(ref bool __result)
        {
            if (!RendererController.ControlsTime) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(scrController), "ProcessKeyInputs")]
    internal static class ProcessKeyInputPatch
    {
        static bool Prefix() => !RendererController.ControlsTime;
    }

    [HarmonyPatch(typeof(scrController), "DebugUpdate")]
    internal static class DebugInputPatch
    {
        static bool Prefix() => !RendererController.ControlsTime;
    }

    [HarmonyPatch(typeof(scnEditor), "HandleKeyboardActions")]
    internal static class EditorKeyboardInputPatch
    {
        static bool Prefix() => !RendererController.ControlsTime;
    }

    [HarmonyPatch(typeof(scnEditor), "TryQuitToMenu")]
    internal static class EditorQuitInputPatch
    {
        static bool Prefix() => !RendererController.ControlsTime;
    }

    [HarmonyPatch(typeof(RDEditorUtils), "CheckForKeyCombo")]
    internal static class EditorKeyComboPatch
    {
        static bool Prefix(ref bool __result)
        {
            if (!RendererController.ControlsTime) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(scrTempEscToQuit), "Update")]
    internal static class TemporaryEscapeInputPatch
    {
        static bool Prefix() => !RendererController.ControlsTime;
    }

    [HarmonyPatch(typeof(scrPlayerManager), "AnyValidInputWasTriggered")]
    internal static class StartAndExitInputPatch
    {
        static bool Prefix(ref bool __result)
        {
            if (!RendererController.ControlsTime) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(scrController), "TogglePauseGame")]
    internal static class RenderPausePatch
    {
        static bool Prefix(scrController __instance, ref bool __result)
        {
            if (RendererController.Instance == null || RendererController.Instance.State != RenderState.Rendering) return true;
            __result = __instance.paused;
            return false;
        }
    }
}
