using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using OrbitRender.Renderer;
using OrbitRender.UI;

namespace OrbitRender.Patches
{
    // Add the renderer to the editor's existing File actions panel so it is
    // available beside New/Open/Save/Save As instead of requiring F6.
    [HarmonyPatch(typeof(scnEditor), "Start")]
    internal static class EditorExportVideoPatch
    {
        private const string ButtonName = "OrbitRender.ExportVideoButton";
        private const string ButtonLabel = "Export Video";

        private static void Postfix(scnEditor __instance)
        {
            try
            {
                InstallButton(__instance);
            }
            catch (Exception ex)
            {
                Main.Entry?.Logger.Error("Could not add Export Video to the editor file menu: " + ex);
            }
        }

        private static void InstallButton(scnEditor editor)
        {
            if (editor == null) return;

            var panelField = AccessTools.Field(typeof(scnEditor), "fileActionsPanel");
            var sourceField = AccessTools.Field(typeof(scnEditor), "buttonSaveAs");
            var panel = panelField?.GetValue(editor) as GameObject;
            var source = sourceField?.GetValue(editor) as Button;
            if (panel == null || source == null)
            {
                Main.Entry?.Logger.Log("Export Video menu button was skipped because the editor file panel is unavailable.");
                return;
            }

            Button button = null;
            foreach (var candidate in panel.GetComponentsInChildren<Button>(true))
            {
                if (candidate.gameObject.name != ButtonName) continue;
                button = candidate;
                break;
            }

            if (button == null)
            {
                var parent = source.transform.IsChildOf(panel.transform)
                    ? source.transform.parent : panel.transform;
                var buttonObject = UnityEngine.Object.Instantiate(source.gameObject, parent);
                buttonObject.name = ButtonName;
                if (parent == source.transform.parent)
                    buttonObject.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
                button = buttonObject.GetComponent<Button>();
            }

            if (button == null) return;

            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() => ExportVideo(editor));
            SetButtonLabel(button.gameObject, ButtonLabel);
            button.gameObject.SetActive(true);
        }

        private static void ExportVideo(scnEditor editor)
        {
            var renderer = RendererController.Instance;
            if (renderer == null)
            {
                editor.ShowNotification("OrbitRender is not ready yet.", null, 4f);
                return;
            }

            if (renderer.Busy)
            {
                editor.ShowNotification(renderer.Message ?? "A render is already in progress.", null, 4f);
                return;
            }

            ExportVideoDialog.Open(editor);
        }

        private static void SetButtonLabel(GameObject buttonObject, string value)
        {
            var tmpTextType = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");
            if (tmpTextType != null)
            {
                foreach (var component in buttonObject.GetComponentsInChildren(tmpTextType, true))
                {
                    var textProperty = tmpTextType.GetProperty("text");
                    if (textProperty != null && textProperty.CanWrite)
                        textProperty.SetValue(component, value, null);
                }
                return;
            }

            var legacyText = buttonObject.GetComponentInChildren<Text>(true);
            if (legacyText != null) legacyText.text = value;
        }
    }
}
