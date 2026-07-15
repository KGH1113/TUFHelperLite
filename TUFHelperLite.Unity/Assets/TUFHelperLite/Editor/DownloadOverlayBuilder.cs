using System;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;

namespace TUFHelperLite.Editor
{
    internal static class DownloadOverlayBuilder
    {
        private const string AssetRoot = "Assets/TUFHelperLite";
        private const string ArtRoot = AssetRoot + "/Art";
        private const string FontRoot = AssetRoot + "/Font";
        private const string ResourcesRoot = AssetRoot + "/Resources";
        private const string PrefabRoot = AssetRoot + "/Prefabs";
        private const string PrefabPath = PrefabRoot + "/DownloadOverlay.prefab";
        private const string PanelSpritePath = ArtRoot + "/rounded-panel.png";
        private const string GradientSpritePath = ArtRoot + "/progress-gradient-v2.png";
        private const string FontSourcePath = FontRoot + "/MAPLESTORY_OTF_BOLD.OTF";
        private const string FontAssetPath = FontRoot + "/MAPLESTORY_OTF_BOLD Dynamic SDF.asset";
        private const string LeadingCharactersPath = ResourcesRoot + "/LineBreaking Leading Characters.txt";
        private const string FollowingCharactersPath = ResourcesRoot + "/LineBreaking Following Characters.txt";
        private const string BundleName = "tufhelperlite_ui.bundle";

        [InitializeOnLoadMethod]
        private static void CreateInitialPrefab()
        {
            if (!File.Exists(Path.Combine(ProjectRoot, PrefabPath)))
            {
                EditorApplication.delayCall += CreateDownloadOverlay;
            }
        }

        [MenuItem("TUFHelperLite/UI/Create Download Overlay")]
        public static void CreateDownloadOverlay()
        {
            EnsureAssetFolders();
            EnsureGeneratedArt();

            Sprite panelSprite = AssetDatabase.LoadAssetAtPath<Sprite>(PanelSpritePath);
            Sprite gradientSprite = AssetDatabase.LoadAssetAtPath<Sprite>(GradientSpritePath);
            Sprite fallbackIcon = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/TUFHelperLite/DifficultyIcons/10006.png")
                ?? AssetDatabase.LoadAssetAtPath<Sprite>("Assets/TUFHelperLite/DifficultyIcons/0.png");
            if (fallbackIcon == null)
            {
                throw new InvalidOperationException("TUF difficulty icons are missing. Run Assets/Sync Difficulty Icons first.");
            }
            TMP_FontAsset font = EnsureFontAsset();

            GameObject root = new GameObject(
                "DownloadOverlay",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));

            Canvas canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10000;

            CanvasScaler scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            Image card = CreateImage("DownloadCard", root.transform, panelSprite, new Color32(7, 7, 10, 232));
            SetRect(card.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(32f, -32f), new Vector2(1240f, 390f));
            card.rectTransform.localScale = Vector3.one * 0.5f;
            card.type = Image.Type.Sliced;

            CanvasGroup downloadCardGroup = card.gameObject.AddComponent<CanvasGroup>();
            downloadCardGroup.alpha = 1f;
            downloadCardGroup.interactable = false;
            downloadCardGroup.blocksRaycasts = false;

            Outline outline = card.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 1f, 1f, 0.11f);
            outline.effectDistance = new Vector2(1f, -1f);

            Image topSheen = CreateImage("TopSheen", card.transform, null, new Color(1f, 1f, 1f, 0.12f));
            SetRect(topSheen.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -1f), new Vector2(-48f, 1f));

            GameObject iconFrame = new GameObject("IconFrame", typeof(RectTransform));
            iconFrame.transform.SetParent(card.transform, false);
            SetRect(iconFrame.GetComponent<RectTransform>(), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(48f, 0f), new Vector2(222f, 222f));

            Image difficultyIcon = CreateImage("DifficultyIcon", iconFrame.transform, fallbackIcon, Color.white);
            SetRect(difficultyIcon.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-14f, -14f));
            difficultyIcon.preserveAspect = true;

            CreateText("MetadataText", card.transform, font, "#12707 - Camellia (かめりあ)", 32f, FontStyles.Normal,
                new Color32(153, 156, 166, 255), TextAlignmentOptions.Left,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(300f, -34f), new Vector2(880f, 42f));

            CreateText("TitleText", card.transform, font, "Hello (BPM) 2026", 64f, FontStyles.Normal,
                new Color32(246, 246, 248, 255), TextAlignmentOptions.Left,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(300f, -80f), new Vector2(880f, 82f));

            CreateText("CreatorLabel", card.transform, font, "Creator", 28f, FontStyles.Normal,
                new Color32(128, 132, 143, 255), TextAlignmentOptions.Left,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(300f, -169f), new Vector2(140f, 40f));

            CreateText("CreatorText", card.transform, font, "한가지", 34f, FontStyles.Normal,
                new Color32(220, 222, 228, 255), TextAlignmentOptions.Left,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(440f, -165f), new Vector2(740f, 44f));

            Image divider = CreateImage("Divider", card.transform, null, new Color(1f, 1f, 1f, 0.08f));
            SetRect(divider.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(300f, -224f), new Vector2(880f, 1f));

            Image statusDot = CreateImage("StatusDot", card.transform, panelSprite, new Color32(68, 191, 255, 255));
            SetRect(statusDot.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(300f, -256f), new Vector2(11f, 11f));
            statusDot.type = Image.Type.Sliced;

            CreateText("StatusText", card.transform, font, "Downloading level archive", 30f, FontStyles.Normal,
                new Color32(180, 184, 194, 255), TextAlignmentOptions.Left,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(324f, -243f), new Vector2(716f, 42f));

            CreateText("ProgressText", card.transform, font, "42%", 30f, FontStyles.Normal,
                new Color32(218, 220, 226, 255), TextAlignmentOptions.Right,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(1060f, -243f), new Vector2(120f, 42f));

            Image progressBackground = CreateImage("ProgressBackground", card.transform, panelSprite, new Color32(43, 45, 52, 210));
            SetRect(progressBackground.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(300f, 66f), new Vector2(880f, 18f));
            progressBackground.type = Image.Type.Sliced;
            Mask progressMask = progressBackground.gameObject.AddComponent<Mask>();
            progressMask.showMaskGraphic = true;

            Image progressFill = CreateImage("ProgressFill", progressBackground.transform, gradientSprite, Color.white);
            SetRect(progressFill.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            progressFill.type = Image.Type.Filled;
            progressFill.fillMethod = Image.FillMethod.Horizontal;
            progressFill.fillOrigin = 0;
            progressFill.fillAmount = 0.42f;

            CreateSelectionModal(root.transform, panelSprite, fallbackIcon, font);
            CreateDiskSpaceWarningModal(root.transform, panelSprite, font);

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            UnityEngine.Object.DestroyImmediate(root);

            AssetImporter importer = AssetImporter.GetAtPath(PrefabPath);
            importer.assetBundleName = BundleName;
            importer.SaveAndReimport();

            GameObject existing = GameObject.Find("DownloadOverlay");
            if (existing != null)
            {
                UnityEngine.Object.DestroyImmediate(existing);
            }

            GameObject preview = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            preview.name = "DownloadOverlay";
            Selection.activeGameObject = preview;
            EditorGUIUtility.PingObject(prefab);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"TUFHelperLite download overlay created at {PrefabPath}");
        }

        [MenuItem("TUFHelperLite/UI/Preview Download Card")]
        public static void PreviewDownloadCard()
        {
            GameObject preview = GameObject.Find("DownloadOverlay");
            if (preview == null)
            {
                CreateDownloadOverlay();
                preview = GameObject.Find("DownloadOverlay");
            }

            preview.transform.Find("DownloadCard")?.gameObject.SetActive(true);
            preview.transform.Find("SelectionLayer")?.gameObject.SetActive(false);
        }

        [MenuItem("TUFHelperLite/UI/Preview File Selection")]
        public static void PreviewFileSelection()
        {
            GameObject preview = GameObject.Find("DownloadOverlay");
            if (preview == null)
            {
                CreateDownloadOverlay();
                preview = GameObject.Find("DownloadOverlay");
            }

            Transform card = preview.transform.Find("DownloadCard");
            Transform layer = preview.transform.Find("SelectionLayer");
            Transform panel = layer?.Find("SelectionPanel");
            Transform list = panel?.Find("SelectionList");
            Transform content = list?.Find("Viewport/Content");
            Transform template = panel?.Find("SelectionRowTemplate");
            if (card == null || layer == null || panel == null || list == null || content == null || template == null)
            {
                throw new InvalidOperationException("The file selection preview hierarchy is incomplete.");
            }

            card.gameObject.SetActive(false);
            layer.gameObject.SetActive(true);
            layer.GetComponent<CanvasGroup>().alpha = 1f;

            for (int i = content.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.DestroyImmediate(content.GetChild(i).gameObject);
            }

            string[] samplePaths =
            {
                "main.adofai",
                "legacy/alternate-chart.adofai",
                "bonus/very-long-folder-name/extra-difficulty.adofai",
                "variants/easy.adofai",
                "variants/hard.adofai",
                "collab/part-one/chart.adofai",
                "collab/part-two/chart.adofai"
            };
            foreach (string samplePath in samplePaths)
            {
                GameObject row = UnityEngine.Object.Instantiate(template.gameObject, content, false);
                row.name = "SelectionPreviewRow";
                row.SetActive(true);
                row.GetComponentInChildren<TMP_Text>(true).text = samplePath;
            }

            panel.Find("CountText").GetComponent<TMP_Text>().text = "7 .adofai files found";
            const float listHeight = 6f * 56f + 5f * 10f;
            list.GetComponent<RectTransform>().SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, listHeight);
            panel.GetComponent<RectTransform>().sizeDelta = new Vector2(860f, 178f + listHeight + 32f);
            Canvas.ForceUpdateCanvases();
            Selection.activeGameObject = layer.gameObject;
        }

        [MenuItem("TUFHelperLite/UI/Preview Storage Warning")]
        public static void PreviewStorageWarning()
        {
            GameObject preview = GameObject.Find("DownloadOverlay");
            if (preview == null)
            {
                CreateDownloadOverlay();
                preview = GameObject.Find("DownloadOverlay");
            }

            Transform card = preview.transform.Find("DownloadCard");
            Transform selection = preview.transform.Find("SelectionLayer");
            Transform warning = preview.transform.Find("DiskWarningLayer");
            if (card == null || selection == null || warning == null)
            {
                throw new InvalidOperationException("The storage warning preview hierarchy is incomplete.");
            }

            card.gameObject.SetActive(false);
            selection.gameObject.SetActive(false);
            warning.gameObject.SetActive(true);
            warning.GetComponent<CanvasGroup>().alpha = 1f;
            warning.Find("WarningPanel").localScale = Vector3.one;
            Selection.activeGameObject = warning.gameObject;
        }

        [MenuItem("TUFHelperLite/Build/Build macOS UI Bundle")]
        public static void BuildMacBundle()
        {
            BuildBundle(BuildTarget.StandaloneOSX, "mac");
        }

        [MenuItem("TUFHelperLite/Build/Build All UI Bundles", priority = 0)]
        public static void BuildAllBundles()
        {
            BuildBundle(BuildTarget.StandaloneOSX, "mac");
            BuildBundle(BuildTarget.StandaloneWindows64, "win");
            BuildBundle(BuildTarget.StandaloneLinux64, "linux");
        }

        [MenuItem("TUFHelperLite/Build/Build Windows UI Bundle")]
        public static void BuildWindowsBundle()
        {
            BuildBundle(BuildTarget.StandaloneWindows64, "win");
        }

        [MenuItem("TUFHelperLite/Build/Build Linux UI Bundle")]
        public static void BuildLinuxBundle()
        {
            BuildBundle(BuildTarget.StandaloneLinux64, "linux");
        }

        private static void BuildBundle(BuildTarget target, string platformFolder)
        {
            if (!File.Exists(Path.Combine(ProjectRoot, PrefabPath)))
            {
                throw new InvalidOperationException("Create the download overlay prefab before building its bundle.");
            }

            DifficultyIconSync.EnsureBundleAssignments();
            string outputDirectory = Path.Combine(ProjectRoot, "Build", "AssetBundles", platformFolder);
            Directory.CreateDirectory(outputDirectory);

            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
                outputDirectory,
                BuildAssetBundleOptions.ChunkBasedCompression,
                target);

            if (manifest == null)
            {
                throw new InvalidOperationException($"Failed to build the {platformFolder} UI bundle.");
            }

            string builtBundle = Path.Combine(outputDirectory, BundleName);
            string modAssetDirectory = Path.Combine(RepositoryRoot, "TUFHelperLite", "Assets", platformFolder);
            Directory.CreateDirectory(modAssetDirectory);
            File.Copy(builtBundle, Path.Combine(modAssetDirectory, BundleName), true);
            AssetDatabase.Refresh();
            Debug.Log($"Built {target} UI bundle and copied it to {modAssetDirectory}");
        }

        private static void CreateSelectionModal(Transform parent, Sprite panelSprite, Sprite fallbackIcon, TMP_FontAsset font)
        {
            GameObject layer = new GameObject("SelectionLayer", typeof(RectTransform), typeof(CanvasGroup));
            layer.transform.SetParent(parent, false);
            SetRect(layer.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

            CanvasGroup layerGroup = layer.GetComponent<CanvasGroup>();
            layerGroup.alpha = 1f;
            layerGroup.interactable = true;
            layerGroup.blocksRaycasts = true;
            layerGroup.ignoreParentGroups = true;

            Image backdrop = CreateImage("SelectionBackdrop", layer.transform, null, new Color(0f, 0f, 0f, 0.52f));
            SetRect(backdrop.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            backdrop.raycastTarget = true;

            Image panel = CreateImage("SelectionPanel", layer.transform, panelSprite, new Color32(7, 7, 10, 245));
            SetRect(panel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(860f, 596f));
            panel.type = Image.Type.Sliced;

            Outline panelOutline = panel.gameObject.AddComponent<Outline>();
            panelOutline.effectColor = new Color(1f, 1f, 1f, 0.13f);
            panelOutline.effectDistance = new Vector2(1f, -1f);

            Image topSheen = CreateImage("TopSheen", panel.transform, null, new Color(1f, 1f, 1f, 0.13f));
            SetRect(topSheen.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -1f), new Vector2(-48f, 1f));

            Image difficultyIcon = CreateImage("DifficultyIcon", panel.transform, fallbackIcon, Color.white);
            SetRect(difficultyIcon.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(32f, -32f), new Vector2(96f, 96f));
            difficultyIcon.preserveAspect = true;

            CreateText("TitleText", panel.transform, font, "Choose a level file", 40f, FontStyles.Normal,
                new Color32(246, 246, 248, 255), TextAlignmentOptions.Left,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(152f, -28f), new Vector2(676f, 52f));

            CreateText("MetadataText", panel.transform, font, "#12707 - Hello (BPM) 2026", 23f, FontStyles.Normal,
                new Color32(174, 177, 187, 255), TextAlignmentOptions.Left,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(152f, -81f), new Vector2(676f, 32f));

            CreateText("CountText", panel.transform, font, "3 .adofai files found", 20f, FontStyles.Normal,
                new Color32(119, 197, 235, 255), TextAlignmentOptions.Left,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(152f, -114f), new Vector2(676f, 28f));

            Image divider = CreateImage("Divider", panel.transform, null, new Color(1f, 1f, 1f, 0.09f));
            SetRect(divider.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(32f, -158f), new Vector2(796f, 1f));

            Image list = CreateImage("SelectionList", panel.transform, null, Color.clear);
            SetRect(list.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -178f), new Vector2(-64f, 386f));
            list.raycastTarget = true;
            ScrollRect scrollRect = list.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.inertia = true;
            scrollRect.decelerationRate = 0.135f;
            scrollRect.scrollSensitivity = 36f;

            GameObject viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewport.transform.SetParent(list.transform, false);
            SetRect(viewport.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(-7f, 0f), new Vector2(-14f, 0f));

            GameObject content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(viewport.transform, false);
            RectTransform contentRect = content.GetComponent<RectTransform>();
            SetRect(contentRect, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), Vector2.zero, Vector2.zero);

            VerticalLayoutGroup layout = content.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            ContentSizeFitter fitter = content.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            Image scrollbarBackground = CreateImage("Scrollbar", list.transform, panelSprite, new Color32(37, 40, 48, 220));
            SetRect(scrollbarBackground.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(0f, 0f), new Vector2(6f, -4f));
            scrollbarBackground.type = Image.Type.Sliced;
            scrollbarBackground.raycastTarget = true;

            GameObject slidingArea = new GameObject("SlidingArea", typeof(RectTransform));
            slidingArea.transform.SetParent(scrollbarBackground.transform, false);
            SetRect(slidingArea.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

            Image handle = CreateImage("Handle", slidingArea.transform, panelSprite, new Color32(68, 191, 255, 235));
            SetRect(handle.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            handle.type = Image.Type.Sliced;
            handle.raycastTarget = true;

            Scrollbar scrollbar = scrollbarBackground.gameObject.AddComponent<Scrollbar>();
            scrollbar.handleRect = handle.rectTransform;
            scrollbar.targetGraphic = handle;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;

            scrollRect.viewport = viewport.GetComponent<RectTransform>();
            scrollRect.content = contentRect;
            scrollRect.verticalScrollbar = scrollbar;
            scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            Button rowTemplate = CreateSelectionRowTemplate(panel.transform, panelSprite, font);
            rowTemplate.gameObject.SetActive(false);
            layer.SetActive(false);
        }

        private static void CreateDiskSpaceWarningModal(Transform parent, Sprite panelSprite, TMP_FontAsset font)
        {
            GameObject layer = new GameObject("DiskWarningLayer", typeof(RectTransform), typeof(CanvasGroup));
            layer.transform.SetParent(parent, false);
            SetRect(layer.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

            CanvasGroup layerGroup = layer.GetComponent<CanvasGroup>();
            layerGroup.alpha = 1f;
            layerGroup.interactable = true;
            layerGroup.blocksRaycasts = true;
            layerGroup.ignoreParentGroups = true;

            Image backdrop = CreateImage("WarningBackdrop", layer.transform, null, new Color(0f, 0f, 0f, 0.58f));
            SetRect(backdrop.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            backdrop.raycastTarget = true;

            Image panel = CreateImage("WarningPanel", layer.transform, panelSprite, new Color32(7, 7, 10, 248));
            SetRect(panel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(720f, 420f));
            panel.type = Image.Type.Sliced;
            panel.raycastTarget = true;

            Outline panelOutline = panel.gameObject.AddComponent<Outline>();
            panelOutline.effectColor = new Color(1f, 1f, 1f, 0.13f);
            panelOutline.effectDistance = new Vector2(1f, -1f);

            Image topSheen = CreateImage("TopSheen", panel.transform, null, new Color(1f, 1f, 1f, 0.13f));
            SetRect(topSheen.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -1f), new Vector2(-48f, 1f));

            Image icon = CreateImage("WarningIcon", panel.transform, panelSprite, new Color32(255, 181, 71, 45));
            SetRect(icon.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(44f, -38f), new Vector2(76f, 76f));
            icon.type = Image.Type.Sliced;

            CreateText("WarningGlyph", icon.transform, font, "!", 46f, FontStyles.Bold,
                new Color32(255, 190, 86, 255), TextAlignmentOptions.Center,
                Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(0f, 2f), Vector2.zero);

            CreateText("EyebrowText", panel.transform, font, "STORAGE WARNING", 18f, FontStyles.Bold,
                new Color32(255, 190, 86, 255), TextAlignmentOptions.Left,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(142f, -42f), new Vector2(520f, 24f));

            CreateText("TitleText", panel.transform, font, "Not enough storage space", 38f, FontStyles.Normal,
                new Color32(246, 246, 248, 255), TextAlignmentOptions.Left,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(142f, -74f), new Vector2(520f, 52f));

            Image divider = CreateImage("Divider", panel.transform, null, new Color(1f, 1f, 1f, 0.09f));
            SetRect(divider.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(36f, -150f), new Vector2(648f, 1f));

            TextMeshProUGUI body = CreateText("BodyText", panel.transform, font,
                "Free up some space and retry the download. Your existing files are safe.",
                22f, FontStyles.Normal, new Color32(174, 177, 187, 255), TextAlignmentOptions.TopLeft,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(44f, -176f), new Vector2(632f, 56f));
            body.textWrappingMode = TextWrappingModes.Normal;
            body.overflowMode = TextOverflowModes.Overflow;

            Image details = CreateImage("DetailsPanel", panel.transform, panelSprite, new Color32(24, 26, 33, 245));
            SetRect(details.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(44f, -248f), new Vector2(632f, 60f));
            details.type = Image.Type.Sliced;

            CreateText("AvailableText", details.transform, font, "Available 842 MB", 21f, FontStyles.Normal,
                new Color32(180, 184, 194, 255), TextAlignmentOptions.MidlineLeft,
                Vector2.zero, Vector2.one, new Vector2(0f, 0.5f), new Vector2(22f, 0f), new Vector2(276f, 0f));

            CreateText("RequiredText", details.transform, font, "Required 2.4 GB", 21f, FontStyles.Normal,
                new Color32(255, 201, 119, 255), TextAlignmentOptions.MidlineRight,
                Vector2.zero, Vector2.one, new Vector2(1f, 0.5f), new Vector2(-22f, 0f), new Vector2(276f, 0f));

            TextMeshProUGUI helper = CreateText("HelperText", panel.transform, font, "Free up space, then try the download again.", 18f, FontStyles.Normal,
                new Color32(128, 132, 143, 255), TextAlignmentOptions.MidlineLeft,
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(44f, 36f), new Vector2(420f, 54f));
            helper.enableAutoSizing = true;
            helper.fontSizeMin = 14f;
            helper.fontSizeMax = 18f;

            Image dismissImage = CreateImage("DismissButton", panel.transform, panelSprite, new Color32(255, 181, 71, 255));
            SetRect(dismissImage.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-44f, 36f), new Vector2(176f, 54f));
            dismissImage.type = Image.Type.Sliced;
            dismissImage.raycastTarget = true;

            Button dismissButton = dismissImage.gameObject.AddComponent<Button>();
            dismissButton.targetGraphic = dismissImage;
            dismissButton.transition = Selectable.Transition.ColorTint;
            ColorBlock buttonColors = dismissButton.colors;
            buttonColors.normalColor = Color.white;
            buttonColors.highlightedColor = new Color32(255, 224, 174, 255);
            buttonColors.pressedColor = new Color32(231, 150, 43, 255);
            buttonColors.selectedColor = buttonColors.highlightedColor;
            buttonColors.disabledColor = new Color32(120, 120, 120, 180);
            buttonColors.colorMultiplier = 1f;
            buttonColors.fadeDuration = 0.12f;
            dismissButton.colors = buttonColors;

            CreateText("Label", dismissImage.transform, font, "Got it", 22f, FontStyles.Bold,
                new Color32(17, 17, 20, 255), TextAlignmentOptions.Center,
                Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

            layer.SetActive(false);
        }

        private static Button CreateSelectionRowTemplate(Transform parent, Sprite panelSprite, TMP_FontAsset font)
        {
            Image row = CreateImage("SelectionRowTemplate", parent, panelSprite, new Color32(24, 26, 33, 245));
            SetRect(row.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(760f, 56f));
            row.type = Image.Type.Sliced;
            row.raycastTarget = true;

            LayoutElement layout = row.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = 56f;
            layout.preferredHeight = 56f;
            layout.flexibleWidth = 1f;

            Button button = row.gameObject.AddComponent<Button>();
            button.targetGraphic = row;
            button.transition = Selectable.Transition.ColorTint;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color32(207, 242, 255, 255);
            colors.pressedColor = new Color32(157, 220, 246, 255);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color32(100, 103, 112, 170);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.12f;
            button.colors = colors;

            Image accent = CreateImage("Accent", row.transform, panelSprite, new Color32(68, 191, 255, 255));
            SetRect(accent.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(4f, -16f));
            accent.type = Image.Type.Sliced;

            CreateText("PathText", row.transform, font, "levels/main.adofai", 22f, FontStyles.Normal,
                new Color32(224, 226, 232, 255), TextAlignmentOptions.MidlineLeft,
                Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(8f, 0f), new Vector2(-44f, 0f));

            return button;
        }

        private static void EnsureAssetFolders()
        {
            Directory.CreateDirectory(Path.Combine(ProjectRoot, ArtRoot));
            Directory.CreateDirectory(Path.Combine(ProjectRoot, FontRoot));
            Directory.CreateDirectory(Path.Combine(ProjectRoot, PrefabRoot));
            AssetDatabase.Refresh();
        }

        private static void EnsureGeneratedArt()
        {
            CreateRoundedPanelTexture();
            CreateProgressGradientTexture();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            ConfigureSprite(PanelSpritePath, new Vector4(24f, 24f, 24f, 24f));
            ConfigureSprite(GradientSpritePath, Vector4.zero);
        }

        private static void CreateRoundedPanelTexture()
        {
            string absolutePath = Path.Combine(ProjectRoot, PanelSpritePath);
            if (File.Exists(absolutePath))
            {
                return;
            }

            const int size = 64;
            const float radius = 14f;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color32[] pixels = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distanceX = Mathf.Max(radius - x, 0f, x - (size - 1f - radius));
                    float distanceY = Mathf.Max(radius - y, 0f, y - (size - 1f - radius));
                    float distance = Mathf.Sqrt(distanceX * distanceX + distanceY * distanceY);
                    byte alpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(radius + 0.5f - distance) * 255f);
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            File.WriteAllBytes(absolutePath, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
        }

        private static void CreateProgressGradientTexture()
        {
            string absolutePath = Path.Combine(ProjectRoot, GradientSpritePath);
            if (File.Exists(absolutePath))
            {
                return;
            }

            const int width = 256;
            const int height = 8;
            Color left = new Color32(66, 224, 205, 255);
            Color middle = new Color32(70, 184, 255, 255);
            Color right = new Color32(102, 119, 255, 255);
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);

            for (int x = 0; x < width; x++)
            {
                float t = x / (width - 1f);
                Color color = t < 0.5f ? Color.Lerp(left, middle, t * 2f) : Color.Lerp(middle, right, (t - 0.5f) * 2f);
                for (int y = 0; y < height; y++)
                {
                    texture.SetPixel(x, y, color);
                }
            }

            texture.Apply();
            File.WriteAllBytes(absolutePath, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
        }

        private static TMP_FontAsset EnsureFontAsset()
        {
            TMP_FontAsset existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
            if (existing != null)
            {
                EnsureTmpSettings();
                AssignDefaultFontAsset(existing);
                return existing;
            }

            EnsureTmpSettings();

            Font source = AssetDatabase.LoadAssetAtPath<Font>(FontSourcePath);
            if (source == null)
            {
                throw new FileNotFoundException("MapleStory Bold font source was not found.", FontSourcePath);
            }

            TMP_FontAsset fontAsset = TMP_FontAsset.CreateFontAsset(
                source,
                72,
                8,
                GlyphRenderMode.SDFAA,
                1024,
                1024,
                AtlasPopulationMode.Dynamic,
                true);
            if (fontAsset == null)
            {
                throw new InvalidOperationException("Failed to create the dynamic MapleStory TMP font asset.");
            }

            fontAsset.name = "MAPLESTORY_OTF_BOLD Dynamic SDF";
            Texture2D atlas = fontAsset.atlasTextures[0];
            Material material = fontAsset.material;
            atlas.name = "MAPLESTORY_OTF_BOLD Atlas";
            material.name = "MAPLESTORY_OTF_BOLD Material";

            AssetDatabase.CreateAsset(fontAsset, FontAssetPath);
            AssetDatabase.AddObjectToAsset(atlas, fontAsset);
            AssetDatabase.AddObjectToAsset(material, fontAsset);
            EditorUtility.SetDirty(fontAsset);
            AssetDatabase.SaveAssets();
            AssignDefaultFontAsset(fontAsset);
            return fontAsset;
        }

        private static void EnsureTmpSettings()
        {
            const string settingsPath = ResourcesRoot + "/TMP Settings.asset";
            TMP_Settings settings = AssetDatabase.LoadAssetAtPath<TMP_Settings>(settingsPath);
            if (settings == null)
            {
                Directory.CreateDirectory(Path.Combine(ProjectRoot, ResourcesRoot));
                settings = ScriptableObject.CreateInstance<TMP_Settings>();
                settings.name = "TMP Settings";
                AssetDatabase.CreateAsset(settings, settingsPath);
            }

            SerializedObject serialized = new SerializedObject(settings);
            SerializedProperty version = serialized.FindProperty("assetVersion");
            if (version != null) version.stringValue = "2";
            SerializedProperty clearDynamicData = serialized.FindProperty("m_ClearDynamicDataOnBuild");
            if (clearDynamicData != null) clearDynamicData.boolValue = false;
            SerializedProperty leadingCharacters = serialized.FindProperty("m_leadingCharacters");
            if (leadingCharacters != null)
            {
                leadingCharacters.objectReferenceValue = AssetDatabase.LoadAssetAtPath<TextAsset>(LeadingCharactersPath);
            }
            SerializedProperty followingCharacters = serialized.FindProperty("m_followingCharacters");
            if (followingCharacters != null)
            {
                followingCharacters.objectReferenceValue = AssetDatabase.LoadAssetAtPath<TextAsset>(FollowingCharactersPath);
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            TMP_Settings.LoadLinebreakingRules();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        private static void AssignDefaultFontAsset(TMP_FontAsset fontAsset)
        {
            TMP_Settings settings = AssetDatabase.LoadAssetAtPath<TMP_Settings>(ResourcesRoot + "/TMP Settings.asset");
            if (settings == null) return;

            SerializedObject serialized = new SerializedObject(settings);
            SerializedProperty defaultFontAsset = serialized.FindProperty("m_defaultFontAsset");
            if (defaultFontAsset != null) defaultFontAsset.objectReferenceValue = fontAsset;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        private static void ConfigureSprite(string path, Vector4 border)
        {
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                return;
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spriteBorder = border;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }

        private static Image CreateImage(string name, Transform parent, Sprite sprite, Color color)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            gameObject.transform.SetParent(parent, false);
            Image image = gameObject.GetComponent<Image>();
            image.sprite = sprite;
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static TextMeshProUGUI CreateText(
            string name,
            Transform parent,
            TMP_FontAsset font,
            string value,
            float size,
            FontStyles style,
            Color color,
            TextAlignmentOptions alignment,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 position,
            Vector2 dimensions)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            gameObject.transform.SetParent(parent, false);
            TextMeshProUGUI text = gameObject.GetComponent<TextMeshProUGUI>();
            text.font = font;
            text.text = value;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            SetRect(text.rectTransform, anchorMin, anchorMax, pivot, position, dimensions);
            return text;
        }

        private static void SetRect(
            RectTransform rect,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 anchoredPosition,
            Vector2 sizeDelta)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;
            rect.localScale = Vector3.one;
        }

        private static string ProjectRoot => Directory.GetParent(Application.dataPath)?.FullName
            ?? throw new InvalidOperationException("Could not resolve the Unity project directory.");

        private static string RepositoryRoot => Directory.GetParent(ProjectRoot)?.FullName
            ?? throw new InvalidOperationException("Could not resolve the TUFHelperLite repository directory.");
    }
}
