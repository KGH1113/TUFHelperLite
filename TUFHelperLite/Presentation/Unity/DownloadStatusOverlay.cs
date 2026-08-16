using System;
using TUFHelperLite.App;
using TUFHelperLite.Domain.Jobs;
using UnityEngine;

namespace TUFHelperLite.Presentation.Unity;

public sealed class DownloadStatusOverlay : MonoBehaviour
{
  private const float SnapshotInterval = 0.1f;

  private static DownloadStatusOverlay _instance;

  private DownloadOverlayView _view;
  private DownloadJobSnapshot _job;
  private float _nextSnapshotAt;
  private float _displayedProgress;
  private string _displayedJobId;
  private string _displayedDiskWarningJobId;

  public static void EnsureInstalled()
  {
    if (!global::AdofaiIpc.AdofaiIpc.IsMainThread)
    {
      global::AdofaiIpc.AdofaiIpc.RunOnMainThread(EnsureInstalled);
      return;
    }

    if (_instance != null) return;

    GameObject gameObject = new("TUFHelperLite Download Status");
    DontDestroyOnLoad(gameObject);
    _instance = gameObject.AddComponent<DownloadStatusOverlay>();
  }

  public static void Uninstall()
  {
    if (!global::AdofaiIpc.AdofaiIpc.IsMainThread)
    {
      global::AdofaiIpc.AdofaiIpc.RunOnMainThread(Uninstall);
      return;
    }

    if (_instance != null) Destroy(_instance.gameObject);
  }

  private void Start()
  {
    try
    {
      _view = DownloadOverlayView.Load();
      Main.Instance?.Log("TUFHelperLite download overlay AssetBundle loaded");
    }
    catch (Exception e)
    {
      Main.Instance?.Warning($"Failed to load download overlay AssetBundle: {e.Message}");
      Main.Instance?.LogException(e);
    }
  }

  private void Update()
  {
    if (Time.unscaledTime >= _nextSnapshotAt)
    {
      _nextSnapshotAt = Time.unscaledTime + SnapshotInterval;
      _job = LevelJobService.ActiveForModal();

      if (_job == null)
      {
        _view?.SetVisible(false);
        _view?.HideSelection();
      }
      else if (_job.WaitingForSelection)
      {
        _view?.SetVisible(false);
        _view?.ShowSelection(_job, LevelJobService.SelectLevel);
      }
      else if (IsDiskSpaceFailure(_job))
      {
        _view?.SetVisible(false);
        _view?.HideSelection();
        if (!string.Equals(_displayedDiskWarningJobId, _job.JobId, StringComparison.Ordinal))
        {
          _displayedDiskWarningJobId = _job.JobId;
          _view?.ShowDiskSpaceWarning(_job, LevelJobService.DismissModal);
        }
      }
      else
      {
        _view?.HideSelection();

        if (!string.Equals(_displayedJobId, _job.JobId, StringComparison.Ordinal))
        {
          _displayedJobId = _job.JobId;
          _displayedProgress = _job.Progress >= 0 ? Mathf.Clamp01((float)_job.Progress) : 0f;
        }

        _view?.Bind(_job, LevelJobService.Cancel);
        _view?.SetVisible(true);
      }
    }

    _view?.Tick(Time.unscaledDeltaTime);

    if (_job == null || _job.WaitingForSelection || IsDiskSpaceFailure(_job) || _view == null) return;

    bool determinate = _job.Progress >= 0;
    float target = determinate
      ? Mathf.Clamp01((float)_job.Progress)
      : 0.12f + Mathf.PingPong(Time.unscaledTime * 0.35f, 0.76f);
    float blend = 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime);
    _displayedProgress = Mathf.Lerp(_displayedProgress, target, blend);
    _view.SetProgress(_displayedProgress, determinate);
  }

  private void OnDestroy()
  {
    if (_instance != this) return;

    _view?.Dispose();
    _instance = null;
  }

  private static bool IsDiskSpaceFailure(DownloadJobSnapshot job)
  {
    return string.Equals(job?.ErrorCode, "insufficient_disk_space", StringComparison.Ordinal);
  }
}
