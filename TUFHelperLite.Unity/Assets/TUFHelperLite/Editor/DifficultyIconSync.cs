using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using UnityEditor;
using UnityEngine;

namespace TUFHelperLite.Editor
{
    internal static class DifficultyIconSync
    {
        private const string ApiUrl = "https://api.tuforums.com/v2/database/difficulties";
        private const string AssetRoot = "Assets/TUFHelperLite/DifficultyIcons";
        private const string BundleName = "tufhelperlite_ui.bundle";

        [MenuItem("TUFHelperLite/Assets/Sync Difficulty Icons")]
        public static void Sync()
        {
            string absoluteRoot = Path.Combine(ProjectRoot, AssetRoot);
            Directory.CreateDirectory(absoluteRoot);

            try
            {
                using WebClient client = new WebClient();
                client.Headers[HttpRequestHeader.UserAgent] = "TUFHelperLite-Unity/1.0";
                string json = client.DownloadString(ApiUrl);
                DifficultyList response = JsonUtility.FromJson<DifficultyList>($"{{\"items\":{json}}}");
                DifficultyEntry[] entries = response?.items ?? Array.Empty<DifficultyEntry>();
                HashSet<string> expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < entries.Length; i++)
                {
                    DifficultyEntry entry = entries[i];
                    if (entry == null || string.IsNullOrWhiteSpace(entry.icon)) continue;

                    if (EditorUtility.DisplayCancelableProgressBar(
                            "TUFHelperLite Difficulty Icons",
                            $"Downloading {entry.name} ({entry.id})",
                            entries.Length == 0 ? 1f : i / (float)entries.Length))
                    {
                        throw new OperationCanceledException("Difficulty icon synchronization was canceled.");
                    }

                    string fileName = $"{entry.id}.png";
                    expectedFiles.Add(fileName);
                    File.WriteAllBytes(Path.Combine(absoluteRoot, fileName), client.DownloadData(entry.icon));
                }

                foreach (string existing in Directory.GetFiles(absoluteRoot, "*.png"))
                {
                    if (!expectedFiles.Contains(Path.GetFileName(existing))) File.Delete(existing);
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                EnsureBundleAssignments();
                Debug.Log($"Synchronized {entries.Length} TUF difficulty icons into {AssetRoot}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        public static void EnsureBundleAssignments()
        {
            string absoluteRoot = Path.Combine(ProjectRoot, AssetRoot);
            if (!Directory.Exists(absoluteRoot)) return;

            foreach (string absolutePath in Directory.GetFiles(absoluteRoot, "*.png"))
            {
                string assetPath = AssetRoot + "/" + Path.GetFileName(absolutePath);
                TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                if (importer == null) continue;

                bool changed = importer.textureType != TextureImporterType.Sprite
                    || importer.spriteImportMode != SpriteImportMode.Single
                    || importer.mipmapEnabled
                    || importer.maxTextureSize != 256
                    || !string.Equals(importer.assetBundleName, BundleName, StringComparison.Ordinal);

                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.maxTextureSize = 256;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.assetBundleName = BundleName;
                if (changed) importer.SaveAndReimport();
            }

            AssetDatabase.RemoveUnusedAssetBundleNames();
        }

        private static string ProjectRoot => Directory.GetParent(Application.dataPath)?.FullName
            ?? throw new InvalidOperationException("Could not resolve the Unity project directory.");

        [Serializable]
        private sealed class DifficultyList
        {
            public DifficultyEntry[] items;
        }

        [Serializable]
        private sealed class DifficultyEntry
        {
            public int id;
            public string name;
            public string icon;
        }
    }
}
