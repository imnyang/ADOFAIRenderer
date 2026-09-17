using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using OrbitRender.Renderer;

namespace OrbitRender.Patches
{
    // ADOFAI normally puts a background Flash on layer 10 / sorting layer
    // Default. That is correct for the game's ordinary camera chain, but the
    // Hall of Mirrors event changes the static background camera to DepthOnly.
    // When the renderer captures all three cameras into one target, that makes
    // a background Flash appear underneath the Hall of Mirrors composite.
    // During rendering only, draw it through the foreground flash pass so it
    // remains above that composite. The original values are restored after the
    // render, including on cancellation and failure.
    internal static class FlashLayerPatch
    {
        private sealed class Snapshot
        {
            public GameObject Object;
            public int Layer;
            public string SortingLayer;
        }

        private static readonly Dictionary<UnityEngine.Renderer, Snapshot> changed =
            new Dictionary<UnityEngine.Renderer, Snapshot>();
        private static readonly AccessTools.FieldRef<ffxFlashPlus, UnityEngine.Renderer> FlashRenderer =
            AccessTools.FieldRefAccess<ffxFlashPlus, UnityEngine.Renderer>("flashRenderer");
        private static bool applied;

        internal static void Apply()
        {
            if (!RendererController.ControlsTime) return;
            // This method is called from RendererController.LateUpdate, so it
            // must stay O(1) after the first render frame. FindObjectsOfTypeAll
            // walks Unity's global object registry and was previously repeated
            // for every output frame, which made large charts dramatically
            // slower.
            if (applied) return;
            applied = true;
            foreach (var flash in Resources.FindObjectsOfTypeAll<ffxFlashPlus>())
            {
                if (flash == null || flash.FG || flash.gameObject == null || !flash.gameObject.scene.IsValid()) continue;
                var renderer = FlashRenderer(flash);
                if (renderer == null || renderer.gameObject == null) continue;
                if (!changed.ContainsKey(renderer))
                {
                    changed.Add(renderer, new Snapshot {
                        Object = renderer.gameObject,
                        Layer = renderer.gameObject.layer,
                        SortingLayer = renderer.sortingLayerName
                    });
                }
                renderer.gameObject.layer = 0;
                renderer.sortingLayerName = "FgFlash";
            }
        }

        internal static void Restore()
        {
            foreach (var pair in changed)
            {
                var snapshot = pair.Value;
                if (snapshot.Object == null) continue;
                snapshot.Object.layer = snapshot.Layer;
                pair.Key.sortingLayerName = snapshot.SortingLayer;
            }
            changed.Clear();
            applied = false;
        }
    }
}
