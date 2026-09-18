using HarmonyLib;
using UnityEngine;

namespace OrbitRender.Patches
{
    // CameraFilterPack_Blur_Movie divides its shader radius and source size by
    // FastFilter without validating the serialized value first. Some level
    // filter instances arrive with FastFilter == 0, which makes every
    // OnRenderImage call throw during capture and leaves Unity with a broken
    // post-process result. One is the filter's normal full-resolution divisor.
    [HarmonyPatch(typeof(CameraFilterPack_Blur_Movie), "OnRenderImage")]
    internal static class CameraFilterPatch
    {
        private static bool logged;

        private static void Prefix(CameraFilterPack_Blur_Movie __instance)
        {
            if (__instance == null || __instance.FastFilter > 0) return;
            __instance.FastFilter = 1;
            if (logged) return;
            logged = true;
            Debug.Log("[OrbitRender] Repaired CameraFilterPack_Blur_Movie FastFilter=0 before rendering.");
        }
    }
}
