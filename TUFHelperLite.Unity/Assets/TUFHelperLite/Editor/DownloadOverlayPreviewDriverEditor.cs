#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace TUFHelperLite.Editor
{
    [CustomEditor(typeof(DownloadOverlayPreviewDriver))]
    internal sealed class DownloadOverlayPreviewDriverEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "Enter Play Mode, then use these controls to preview the update UI without a live update check.",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                DownloadOverlayPreviewDriver driver = (DownloadOverlayPreviewDriver)target;
                if (GUILayout.Button("Preview Checking Update")) driver.PreviewCheckingUpdate();
                if (GUILayout.Button("Preview Update Warning")) driver.PreviewUpdateWarning();
                if (GUILayout.Button("Reset Preview")) driver.ResetPreview();
            }
        }
    }
}
#endif
