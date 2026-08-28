#if UNITY_EDITOR
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TUFHelperLite.Editor
{
    [DisallowMultipleComponent]
    public sealed class DownloadOverlayPreviewDriver : MonoBehaviour
    {
        private const float VisibleDuration = 5f;
        private const float ShowDuration = 0.16f;
        private const float HideDuration = 0.12f;

        private RectTransform rootRect;
        private GameObject card;
        private TMP_Text statusText;
        private TMP_Text progressText;
        private Image downloadProgressFill;
        private RectTransform toastLayer;
        private CanvasGroup toastCanvasGroup;
        private RectTransform toastPanel;
        private Image toastProgressFill;
        private Button dismissButton;
        private PreviewToastState toastState;
        private float toastStateElapsed;
        private float toastRemaining;
        private bool toastHovered;
        private bool checkingUpdate;

        private enum PreviewToastState
        {
            Hidden,
            Showing,
            Visible,
            Hiding
        }

        private void Awake()
        {
            BindHierarchy();
            ResetPreview();
        }

        private void OnEnable()
        {
            if (Application.isPlaying) BindHierarchy();
        }

        private void Update()
        {
            if (!Application.isPlaying) return;

            if (checkingUpdate && downloadProgressFill != null)
            {
                downloadProgressFill.fillAmount = 0.14f + Mathf.PingPong(Time.unscaledTime * 0.55f, 0.72f);
            }

            TickToast(Time.unscaledDeltaTime);
        }

        public void PreviewCheckingUpdate()
        {
            BindHierarchy();
            checkingUpdate = true;
            card.SetActive(true);
            statusText.text = "Checking Update";
            progressText.text = string.Empty;
        }

        public void PreviewUpdateWarning()
        {
            BindHierarchy();
            toastRemaining = VisibleDuration;
            toastProgressFill.fillAmount = 1f;
            toastHovered = false;
            toastState = PreviewToastState.Showing;
            toastStateElapsed = 0f;
            toastLayer.gameObject.SetActive(true);
            toastCanvasGroup.alpha = 0f;
            toastCanvasGroup.interactable = true;
            toastCanvasGroup.blocksRaycasts = true;
            UpdateToastPlacement(-8f);
        }

        public void ResetPreview()
        {
            BindHierarchy();
            checkingUpdate = false;
            toastHovered = false;
            toastState = PreviewToastState.Hidden;
            if (card != null) card.SetActive(false);
            if (toastLayer != null) toastLayer.gameObject.SetActive(false);
        }

        private void BindHierarchy()
        {
            rootRect = transform as RectTransform;
            card = transform.Find("DownloadCard")?.gameObject;
            statusText = transform.Find("DownloadCard/StatusText")?.GetComponent<TMP_Text>();
            progressText = transform.Find("DownloadCard/ProgressText")?.GetComponent<TMP_Text>();
            downloadProgressFill = transform.Find("DownloadCard/ProgressBackground/ProgressFill")?.GetComponent<Image>();
            toastLayer = transform.Find("UpdateToastLayer") as RectTransform;
            toastCanvasGroup = toastLayer?.GetComponent<CanvasGroup>();
            toastPanel = transform.Find("UpdateToastLayer/ToastPanel") as RectTransform;
            toastProgressFill = transform.Find("UpdateToastLayer/ToastPanel/ProgressTrack/ProgressFill")?.GetComponent<Image>();
            dismissButton = transform.Find("UpdateToastLayer/ToastPanel/DismissButton")?.GetComponent<Button>();

            if (toastPanel != null && toastPanel.GetComponent<PreviewToastHoverTrigger>() == null)
            {
                PreviewToastHoverTrigger trigger = toastPanel.gameObject.AddComponent<PreviewToastHoverTrigger>();
                trigger.Configure(SetHovered);
            }

            if (dismissButton != null)
            {
                dismissButton.onClick.RemoveListener(DismissToast);
                dismissButton.onClick.AddListener(DismissToast);
            }
        }

        private void TickToast(float deltaTime)
        {
            if (toastState == PreviewToastState.Hidden || toastLayer == null) return;

            toastStateElapsed += deltaTime;
            switch (toastState)
            {
                case PreviewToastState.Showing:
                {
                    float progress = Mathf.Clamp01(toastStateElapsed / ShowDuration);
                    float eased = 1f - Mathf.Pow(1f - progress, 3f);
                    toastCanvasGroup.alpha = eased;
                    UpdateToastPlacement(Mathf.Lerp(-8f, 0f, eased));
                    if (progress >= 1f)
                    {
                        toastState = PreviewToastState.Visible;
                        toastStateElapsed = 0f;
                    }
                    break;
                }
                case PreviewToastState.Visible:
                    UpdateToastPlacement(0f);
                    if (!toastHovered)
                    {
                        toastRemaining = Mathf.Max(0f, toastRemaining - deltaTime);
                        toastProgressFill.fillAmount = toastRemaining / VisibleDuration;
                        if (toastRemaining <= 0f) DismissToast();
                    }
                    break;
                case PreviewToastState.Hiding:
                {
                    float progress = Mathf.Clamp01(toastStateElapsed / HideDuration);
                    toastCanvasGroup.alpha = 1f - progress;
                    UpdateToastPlacement(0f);
                    if (progress >= 1f)
                    {
                        toastState = PreviewToastState.Hidden;
                        toastLayer.gameObject.SetActive(false);
                    }
                    break;
                }
            }
        }

        private void DismissToast()
        {
            if (toastState == PreviewToastState.Hidden || toastState == PreviewToastState.Hiding) return;

            toastHovered = false;
            toastState = PreviewToastState.Hiding;
            toastStateElapsed = 0f;
            toastCanvasGroup.interactable = false;
            toastCanvasGroup.blocksRaycasts = false;
        }

        private void SetHovered(bool hovered)
        {
            toastHovered = hovered && toastState == PreviewToastState.Visible;
        }

        private void UpdateToastPlacement(float verticalOffset)
        {
            if (rootRect == null || toastPanel == null) return;

            Rect canvasBounds = rootRect.rect;
            Rect panelBounds = toastPanel.rect;
            float right = canvasBounds.xMax - 24f;
            float bottom = canvasBounds.yMin + 112f + verticalOffset;
            right = Mathf.Clamp(right, canvasBounds.xMin + panelBounds.width + 24f, canvasBounds.xMax - 24f);
            bottom = Mathf.Clamp(bottom, canvasBounds.yMin + 24f, canvasBounds.yMax - panelBounds.height - 24f);
            toastPanel.anchoredPosition = new Vector2(right, bottom);
        }

        private void OnDisable()
        {
            toastHovered = false;
            if (dismissButton != null) dismissButton.onClick.RemoveListener(DismissToast);
        }
    }

    public sealed class PreviewToastHoverTrigger : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private System.Action<bool> hoverChanged;

        public void Configure(System.Action<bool> callback)
        {
            hoverChanged = callback;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            hoverChanged?.Invoke(true);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            hoverChanged?.Invoke(false);
        }

        private void OnDisable()
        {
            hoverChanged?.Invoke(false);
        }
    }
}
#endif
