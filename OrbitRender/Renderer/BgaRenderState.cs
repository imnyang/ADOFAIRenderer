using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace OrbitRender.Renderer
{
    // BGA mode keeps the level's camera, timing, background and decorations,
    // while hiding the gameplay-only visuals that should not be baked into a
    // background animation. The original enabled state is restored on exit.
    internal sealed class BgaRenderState : IDisposable
    {
        private sealed class RendererSnapshot
        {
            public UnityEngine.Renderer Renderer;
            public bool Enabled;
        }

        private static readonly BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly List<RendererSnapshot> renderers = new List<RendererSnapshot>();
        private readonly HashSet<UnityEngine.Renderer> seen = new HashSet<UnityEngine.Renderer>();
        private bool disposed;

        public int HiddenRendererCount => renderers.Count;

        private BgaRenderState() { }

        public static BgaRenderState Capture()
        {
            var state = new BgaRenderState();
            // Include inactive future tiles/holds as well. They can become
            // visible later in the chart, so active-only discovery would let
            // them flash into a BGA frame after their first activation.
            foreach (var floor in Resources.FindObjectsOfTypeAll<scrFloor>())
                if (IsSceneObject(floor)) state.CaptureFloor(floor);
            foreach (var planet in Resources.FindObjectsOfTypeAll<scrPlanet>())
                if (IsSceneObject(planet)) state.CaptureObject(planet.gameObject);
            foreach (var hold in Resources.FindObjectsOfTypeAll<scrHoldRenderer>())
                if (IsSceneObject(hold)) state.CaptureObject(hold.gameObject);
            state.Apply();
            return state;
        }

        private static bool IsSceneObject(Component component)
        {
            return component != null && component.gameObject != null && component.gameObject.scene.IsValid();
        }

        private void CaptureFloor(scrFloor floor)
        {
            if (floor == null) return;
            foreach (var field in typeof(scrFloor).GetFields(InstanceFields))
            {
                if (!IsVisualField(field.Name)) continue;
                try { CaptureValue(field.GetValue(floor)); }
                catch { /* A removed optional floor visual should not abort a render. */ }
            }
        }

        private static bool IsVisualField(string name)
        {
            return name.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("glow", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("icon", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("outline", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("hold", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("vfx", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("multiplanet", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void CaptureValue(object value)
        {
            var renderer = value as UnityEngine.Renderer;
            if (renderer != null)
            {
                Add(renderer);
                return;
            }
            var gameObject = value as GameObject;
            if (gameObject != null)
            {
                CaptureObject(gameObject);
                return;
            }
            var component = value as Component;
            if (component == null) return;
            var typeName = component.GetType().Name;
            if (typeName == "scrVfx" || typeName == "scrVfxPlus") return;
            if (typeName == "FloorRenderer")
            {
                CaptureNamedRenderer(component, "renderer");
                return;
            }
            if (typeName == "scrHoldRenderer")
            {
                CaptureNamedRenderer(component, "m_meshRenderer");
                CaptureNamedRenderer(component, "endCircle");
                return;
            }
            CaptureObject(component.gameObject);
        }

        private void CaptureNamedRenderer(Component component, string fieldName)
        {
            var field = component.GetType().GetField(fieldName, InstanceFields);
            if (field == null) return;
            Add(field.GetValue(component) as UnityEngine.Renderer);
        }

        private void CaptureObject(GameObject gameObject)
        {
            if (gameObject == null) return;
            foreach (var renderer in gameObject.GetComponentsInChildren<UnityEngine.Renderer>(true))
                Add(renderer);
        }

        private void Add(UnityEngine.Renderer renderer)
        {
            if (renderer == null || !seen.Add(renderer)) return;
            renderers.Add(new RendererSnapshot { Renderer = renderer, Enabled = renderer.enabled });
        }

        public void Apply()
        {
            if (disposed) return;
            for (var i = 0; i < renderers.Count; i++)
            {
                var renderer = renderers[i].Renderer;
                if (renderer != null && renderer.enabled) renderer.enabled = false;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            for (var i = 0; i < renderers.Count; i++)
            {
                var snapshot = renderers[i];
                if (snapshot.Renderer != null) snapshot.Renderer.enabled = snapshot.Enabled;
            }
            renderers.Clear();
            seen.Clear();
        }
    }
}
