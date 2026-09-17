using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace OrbitRender.Renderer
{
    // Keeps the game's text lifecycle running while selectively preventing
    // disabled categories from reaching captured frames. This lets countdown
    // and result logic complete normally and restores the editor UI afterward.
    internal sealed class DefaultTextRenderState : IDisposable
    {
        private sealed class TextSnapshot
        {
            internal Behaviour Text;
            internal bool Enabled;
            internal bool VisibleDuringRender;
        }

        private readonly List<TextSnapshot> controlledTexts = new List<TextSnapshot>();
        private readonly HashSet<Behaviour> seen = new HashSet<Behaviour>();
        private readonly HashSet<Behaviour> visibleTexts = new HashSet<Behaviour>();
        private bool disposed;

        internal Canvas CaptureCanvas { get; private set; }

        private DefaultTextRenderState() { }

        internal static DefaultTextRenderState Capture(bool showSongTitle, bool showCountdown,
            bool showResultText)
        {
            var state = new DefaultTextRenderState();
            var controller = ADOBase.controller;
            var ui = scrUIController.instance;

            if (ui != null && (showSongTitle || showCountdown || showResultText))
                state.CaptureCanvas = ui.canvas != null ? ui.canvas.rootCanvas : null;

            if (showSongTitle)
            {
                state.Allow(controller != null ? controller.txtLevelName : null);
                state.Allow(ui != null ? ui.txtLevelName : null);
            }

            if (showCountdown)
                state.Allow(ui != null ? ui.txtCountdown : null);

            if (showResultText)
            {
                state.Allow(controller != null ? controller.txtCongrats : null);
                state.Allow(controller != null ? controller.txtAprilCongrats : null);
                state.Allow(ui != null ? ui.txtCongrats : null);
                state.Allow(ui != null ? ui.txtAprilCongrats : null);
            }

            // The gameplay HUD shares one screen-space canvas with pause,
            // autoplay, modifier, and editor controls. Preserve only the text
            // categories explicitly selected for the exported frame.
            if (state.CaptureCanvas != null)
                foreach (var graphic in state.CaptureCanvas.GetComponentsInChildren<Graphic>(true))
                    if (!state.visibleTexts.Contains(graphic)) state.Hide(graphic);

            state.Apply();
            return state;
        }

        private void Allow(Behaviour text)
        {
            if (text == null || !visibleTexts.Add(text)) return;
            Add(text, true);
        }

        private void Hide(Behaviour text)
        {
            Add(text, false);
        }

        private void Add(Behaviour text, bool visibleDuringRender)
        {
            if (text == null || !seen.Add(text)) return;
            controlledTexts.Add(new TextSnapshot {
                Text = text,
                Enabled = text.enabled,
                VisibleDuringRender = visibleDuringRender
            });
        }

        internal void Apply()
        {
            if (disposed) return;
            for (var i = 0; i < controlledTexts.Count; i++)
            {
                var snapshot = controlledTexts[i];
                var text = snapshot.Text;
                if (text != null && text.enabled != snapshot.VisibleDuringRender)
                    text.enabled = snapshot.VisibleDuringRender;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            for (var i = 0; i < controlledTexts.Count; i++)
            {
                var snapshot = controlledTexts[i];
                if (snapshot.Text != null) snapshot.Text.enabled = snapshot.Enabled;
            }
            controlledTexts.Clear();
            seen.Clear();
            visibleTexts.Clear();
            CaptureCanvas = null;
        }
    }
}
