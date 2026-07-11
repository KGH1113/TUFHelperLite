using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using TUFHelperLite.Domain.Jobs;
using UnityEngine;
using UnityEngine.UI;

namespace TUFHelperLite.Presentation.Unity;

internal sealed class DownloadOverlayView : IDisposable
{
  private const string BundleName = "tufhelperlite_ui.bundle";
  private const float SelectionPanelWidth = 860f;
  private const float SelectionListTop = 178f;
  private const float SelectionRowHeight = 56f;
  private const float SelectionRowSpacing = 10f;
  private const float SelectionBottomPadding = 32f;
  private const int SelectionVisibleRows = 6;

  private readonly AssetBundle _bundle;
  private readonly GameObject _root;
  private readonly CanvasGroup _downloadCanvasGroup;
  private readonly RectTransform _card;
  private readonly TMP_Text _metadataText;
  private readonly TMP_Text _titleText;
  private readonly TMP_Text _creatorText;
  private readonly TMP_Text _statusText;
  private readonly TMP_Text _progressText;
  private readonly Image _difficultyIcon;
  private readonly Image _progressFill;
  private readonly Image _statusDot;
  private readonly Sprite _fallbackIcon;
  private readonly Dictionary<int, string> _difficultyAssetNames = new();
  private readonly Dictionary<int, Sprite> _difficultyIcons = new();
  private readonly Vector2 _restingCardPosition;
  private readonly Vector3 _restingCardScale;
  private readonly RectTransform _selectionLayer;
  private readonly CanvasGroup _selectionCanvasGroup;
  private readonly RectTransform _selectionPanel;
  private readonly RectTransform _selectionList;
  private readonly RectTransform _selectionContent;
  private readonly Button _selectionRowTemplate;
  private readonly ScrollRect _selectionScrollRect;
  private readonly TMP_Text _selectionMetadataText;
  private readonly TMP_Text _selectionCountText;
  private readonly Image _selectionDifficultyIcon;
  private readonly Vector3 _selectionPanelScale;
  private bool _targetVisible;
  private bool _selectionTargetVisible;
  private string _selectionKey;

  private DownloadOverlayView(AssetBundle bundle, GameObject root)
  {
    _bundle = bundle;
    _root = root;
    _card = Find<RectTransform>("DownloadCard");
    _downloadCanvasGroup = _card.GetComponent<CanvasGroup>();
    _metadataText = Find<TextMeshProUGUI>("DownloadCard/MetadataText");
    _titleText = Find<TextMeshProUGUI>("DownloadCard/TitleText");
    _creatorText = Find<TextMeshProUGUI>("DownloadCard/CreatorText");
    _statusText = Find<TextMeshProUGUI>("DownloadCard/StatusText");
    _progressText = Find<TextMeshProUGUI>("DownloadCard/ProgressText");
    _difficultyIcon = Find<Image>("DownloadCard/IconFrame/DifficultyIcon");
    _progressFill = Find<Image>("DownloadCard/ProgressBackground/ProgressFill");
    _statusDot = Find<Image>("DownloadCard/StatusDot");
    _restingCardPosition = _card.anchoredPosition;
    _restingCardScale = _card.localScale;
    _selectionLayer = Find<RectTransform>("SelectionLayer");
    _selectionCanvasGroup = _selectionLayer.GetComponent<CanvasGroup>();
    _selectionPanel = Find<RectTransform>("SelectionLayer/SelectionPanel");
    _selectionList = Find<RectTransform>("SelectionLayer/SelectionPanel/SelectionList");
    _selectionContent = Find<RectTransform>("SelectionLayer/SelectionPanel/SelectionList/Viewport/Content");
    _selectionRowTemplate = Find<Button>("SelectionLayer/SelectionPanel/SelectionRowTemplate");
    _selectionScrollRect = _selectionList.GetComponent<ScrollRect>();
    _selectionMetadataText = Find<TextMeshProUGUI>("SelectionLayer/SelectionPanel/MetadataText");
    _selectionCountText = Find<TextMeshProUGUI>("SelectionLayer/SelectionPanel/CountText");
    _selectionDifficultyIcon = Find<Image>("SelectionLayer/SelectionPanel/DifficultyIcon");
    _selectionPanelScale = _selectionPanel.localScale;
    IndexDifficultyIcons();
    _fallbackIcon = LoadDifficultyIcon(0) ?? _difficultyIcon.sprite;
    _selectionRowTemplate.gameObject.SetActive(false);
    _card.gameObject.SetActive(false);
    _selectionLayer.gameObject.SetActive(false);
    _root.SetActive(true);
  }

  public static DownloadOverlayView Load()
  {
    string path = Path.Combine(ModDirectory(), "Assets", PlatformFolder(), BundleName);
    if (!File.Exists(path))
    {
      throw new FileNotFoundException("TUFHelperLite UI AssetBundle was not found.", path);
    }

    AssetBundle bundle = AssetBundle.LoadFromFile(path);
    if (bundle == null)
    {
      throw new InvalidOperationException($"Failed to load TUFHelperLite UI AssetBundle: {path}");
    }

    string prefabName = bundle.GetAllAssetNames()
      .FirstOrDefault(name => name.EndsWith("/downloadoverlay.prefab", StringComparison.OrdinalIgnoreCase));
    GameObject prefab = string.IsNullOrWhiteSpace(prefabName)
      ? null
      : bundle.LoadAsset<GameObject>(prefabName);

    if (prefab == null)
    {
      bundle.Unload(true);
      throw new InvalidOperationException("DownloadOverlay prefab was not found in the UI AssetBundle.");
    }

    GameObject root = UnityEngine.Object.Instantiate(prefab);
    root.name = "TUFHelperLite Download Overlay";
    UnityEngine.Object.DontDestroyOnLoad(root);
    return new DownloadOverlayView(bundle, root);
  }

  public void SetVisible(bool visible)
  {
    if (_root == null || _targetVisible == visible) return;

    _targetVisible = visible;
    if (visible && !_card.gameObject.activeSelf)
    {
      _card.gameObject.SetActive(true);
      _downloadCanvasGroup.alpha = 0f;
      _card.anchoredPosition = _restingCardPosition + new Vector2(0f, -18f);
      _card.localScale = _restingCardScale * 0.985f;
    }
  }

  public void ShowSelection(DownloadJobSnapshot job, Func<string, string, bool> onSelect)
  {
    if (job == null || !job.WaitingForSelection) return;

    string[] paths = job.LevelPaths ?? Array.Empty<string>();
    string key = job.JobId + "\n" + string.Join("\n", paths);
    if (!string.Equals(_selectionKey, key, StringComparison.Ordinal))
    {
      _selectionKey = key;
      RebuildSelection(job, paths, onSelect);
    }

    if (_selectionTargetVisible) return;

    _selectionTargetVisible = true;
    _selectionLayer.gameObject.SetActive(true);
    _selectionCanvasGroup.alpha = 0f;
    _selectionCanvasGroup.interactable = true;
    _selectionCanvasGroup.blocksRaycasts = true;
    _selectionPanel.localScale = _selectionPanelScale * 0.98f;
  }

  public void HideSelection()
  {
    if (!_selectionTargetVisible) return;

    _selectionTargetVisible = false;
    _selectionCanvasGroup.interactable = false;
    _selectionCanvasGroup.blocksRaycasts = false;
  }

  public void Tick(float deltaTime)
  {
    if (_root == null) return;

    float blend = 1f - Mathf.Exp(-12f * deltaTime);
    if (_card.gameObject.activeSelf)
    {
      float targetAlpha = _targetVisible ? 1f : 0f;
      _downloadCanvasGroup.alpha = Mathf.Lerp(_downloadCanvasGroup.alpha, targetAlpha, blend);
      _card.anchoredPosition = Vector2.Lerp(
        _card.anchoredPosition,
        _restingCardPosition + (_targetVisible ? Vector2.zero : new Vector2(0f, -8f)),
        blend);
      _card.localScale = Vector3.Lerp(
        _card.localScale,
        _restingCardScale * (_targetVisible ? 1f : 0.992f),
        blend);

      if (!_targetVisible && _downloadCanvasGroup.alpha < 0.01f)
      {
        _card.gameObject.SetActive(false);
      }
    }

    if (_selectionLayer.gameObject.activeSelf)
    {
      float targetAlpha = _selectionTargetVisible ? 1f : 0f;
      _selectionCanvasGroup.alpha = Mathf.Lerp(_selectionCanvasGroup.alpha, targetAlpha, blend);
      _selectionPanel.localScale = Vector3.Lerp(
        _selectionPanel.localScale,
        _selectionPanelScale * (_selectionTargetVisible ? 1f : 0.985f),
        blend);

      if (!_selectionTargetVisible && _selectionCanvasGroup.alpha < 0.01f)
      {
        _selectionLayer.gameObject.SetActive(false);
      }
    }
  }

  public void Bind(DownloadJobSnapshot job)
  {
    _metadataText.text = MetaText(job);
    _titleText.text = FirstNonEmpty(job.Song, LevelNameFromPath(job.SelectedLevelPath), "Downloading level");
    _creatorText.text = FirstNonEmpty(job.Creator, "Unknown creator");
    _statusText.text = StatusText(job);
    _difficultyIcon.sprite = GetDifficultyIcon(job.DifficultyId);
    _statusDot.color = StatusColor(job.Status);
  }

  public void SetProgress(float progress, bool determinate)
  {
    _progressFill.fillAmount = Mathf.Clamp01(progress);
    _progressText.text = determinate ? $"{Mathf.Clamp01(progress) * 100f:0}%" : string.Empty;
  }

  private void RebuildSelection(
    DownloadJobSnapshot job,
    string[] paths,
    Func<string, string, bool> onSelect)
  {
    for (int i = _selectionContent.childCount - 1; i >= 0; i--)
    {
      UnityEngine.Object.Destroy(_selectionContent.GetChild(i).gameObject);
    }

    _selectionMetadataText.text = SelectionMetaText(job);
    _selectionCountText.text = $"{paths.Length} .adofai files found";
    _selectionDifficultyIcon.sprite = GetDifficultyIcon(job.DifficultyId);

    int visibleRows = Mathf.Clamp(paths.Length, 1, SelectionVisibleRows);
    float listHeight = visibleRows * SelectionRowHeight + Mathf.Max(0, visibleRows - 1) * SelectionRowSpacing;
    _selectionList.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, listHeight);
    _selectionPanel.sizeDelta = new Vector2(
      SelectionPanelWidth,
      SelectionListTop + listHeight + SelectionBottomPadding);

    foreach (string path in paths)
    {
      string selectedPath = path;
      Button row = UnityEngine.Object.Instantiate(_selectionRowTemplate, _selectionContent, false);
      row.name = $"SelectionRow-{Path.GetFileNameWithoutExtension(path)}";
      row.gameObject.SetActive(true);
      row.onClick.RemoveAllListeners();
      row.GetComponentInChildren<TMP_Text>(true).text = RelativeLevelPath(job.Directory, path);
      row.onClick.AddListener(() =>
      {
        _selectionCanvasGroup.interactable = false;
        if (onSelect(job.JobId, selectedPath))
        {
          HideSelection();
        }
        else
        {
          _selectionCanvasGroup.interactable = true;
        }
      });
    }

    Canvas.ForceUpdateCanvases();
    _selectionScrollRect.verticalNormalizedPosition = 1f;
  }

  public void Dispose()
  {
    if (_root != null)
    {
      UnityEngine.Object.Destroy(_root);
    }

    _bundle?.Unload(false);
  }

  private T Find<T>(string path) where T : Component
  {
    Transform child = _root.transform.Find(path);
    T component = child == null ? null : child.GetComponent<T>();
    if (component == null)
    {
      throw new InvalidOperationException($"UI component not found: {path} ({typeof(T).Name})");
    }

    return component;
  }

  private void IndexDifficultyIcons()
  {
    foreach (string assetName in _bundle.GetAllAssetNames())
    {
      string normalized = assetName.Replace('\\', '/');
      if (!normalized.Contains("/difficultyicons/") || !normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      if (int.TryParse(Path.GetFileNameWithoutExtension(normalized), out int difficultyId))
      {
        _difficultyAssetNames[difficultyId] = assetName;
      }
    }
  }

  private Sprite GetDifficultyIcon(int difficultyId)
  {
    if (_difficultyIcons.TryGetValue(difficultyId, out Sprite cached)) return cached;
    Sprite sprite = LoadDifficultyIcon(difficultyId);
    if (sprite == null) return _fallbackIcon;

    _difficultyIcons[difficultyId] = sprite;
    return sprite;
  }

  private Sprite LoadDifficultyIcon(int difficultyId)
  {
    return _difficultyAssetNames.TryGetValue(difficultyId, out string assetName)
      ? _bundle.LoadAsset<Sprite>(assetName)
      : null;
  }

  private static string PlatformFolder()
  {
    return Application.platform switch
    {
      RuntimePlatform.OSXPlayer => "mac",
      RuntimePlatform.WindowsPlayer => "win",
      RuntimePlatform.LinuxPlayer => "linux",
      _ => throw new PlatformNotSupportedException($"Unsupported platform: {Application.platform}")
    };
  }

  private static string ModDirectory()
  {
    string assemblyPath = Assembly.GetExecutingAssembly().Location;
    string directory = string.IsNullOrWhiteSpace(assemblyPath)
      ? Directory.GetCurrentDirectory()
      : Path.GetDirectoryName(assemblyPath);

    if (string.Equals(Path.GetFileName(directory), "assembly_cache", StringComparison.OrdinalIgnoreCase))
    {
      string parent = Directory.GetParent(directory)?.FullName;
      if (!string.IsNullOrWhiteSpace(parent)) return parent;
    }

    return directory;
  }

  private static string MetaText(DownloadJobSnapshot job)
  {
    string id = string.IsNullOrWhiteSpace(job.LevelId) ? "TUFHelperLite" : $"#{job.LevelId}";
    return $"{id} - {FirstNonEmpty(job.Artist, "TUF Forums")}";
  }

  private static string SelectionMetaText(DownloadJobSnapshot job)
  {
    string id = string.IsNullOrWhiteSpace(job.LevelId) ? "TUFHelperLite" : $"#{job.LevelId}";
    return $"{id} - {FirstNonEmpty(job.Song, job.Artist, "Downloaded level")}";
  }

  private static string RelativeLevelPath(string directory, string path)
  {
    if (string.IsNullOrWhiteSpace(path)) return "Unknown level file";

    try
    {
      if (!string.IsNullOrWhiteSpace(directory))
      {
        string relative = Path.GetRelativePath(directory, path);
        string parentPrefix = ".." + Path.DirectorySeparatorChar;
        if (
          !string.IsNullOrWhiteSpace(relative) &&
          !Path.IsPathRooted(relative) &&
          !string.Equals(relative, "..", StringComparison.Ordinal) &&
          !relative.StartsWith(parentPrefix, StringComparison.Ordinal))
        {
          return relative.Replace('\\', '/');
        }
      }
    }
    catch (Exception)
    {
      // Fall back to the file name when the paths cannot be relativized.
    }

    return Path.GetFileName(path);
  }

  private static string StatusText(DownloadJobSnapshot job)
  {
    string message = FirstNonEmpty(job.Message, job.Stage, job.Status);
    if (job.Status == "queued" && job.QueuePosition > 0)
    {
      return $"Queued #{job.QueuePosition}: {message}";
    }

    if (job.Status == "failed" && !string.IsNullOrWhiteSpace(job.Error))
    {
      return $"Failed: {job.Error}";
    }

    string label = !string.IsNullOrWhiteSpace(job.Stage) && job.Status == "running"
      ? job.Stage
      : FirstNonEmpty(job.Status, "working");
    return $"{label}: {message}";
  }

  private static Color StatusColor(string status)
  {
    return status switch
    {
      "failed" => new Color32(255, 83, 83, 255),
      "queued" => new Color32(143, 147, 158, 255),
      "completed" => new Color32(91, 221, 152, 255),
      _ => new Color32(68, 191, 255, 255)
    };
  }

  private static string LevelNameFromPath(string path)
  {
    return string.IsNullOrWhiteSpace(path) ? null : Path.GetFileNameWithoutExtension(path);
  }

  private static string FirstNonEmpty(params string[] values)
  {
    foreach (string value in values)
    {
      if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
    }

    return string.Empty;
  }
}
