using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using TUFHelperLite.Domain.Jobs;
using TUFHelperLite.Domain.Levels;
using TUFHelperLite.Infrastructure.Downloads;
using TUFHelperLite.Infrastructure.Tuforums;
using TUFHelperLite.Presentation.Unity;

namespace TUFHelperLite.App;

public static class LevelJobService
{
  private const int MaxRetainedTerminalJobs = 100;
  private static readonly TimeSpan PresentationWaitTimeout = TimeSpan.FromSeconds(2);

  private sealed class QueuedWork
  {
    public QueuedWork(DownloadJob job, Action action)
    {
      Job = job;
      Action = action;
    }

    public DownloadJob Job { get; }
    public Action Action { get; }
  }

  private static readonly object Lock = new();
  private static readonly Dictionary<string, DownloadJob> Jobs = new();
  private static readonly HashSet<string> DismissedModalJobIds = new();
  private static readonly Queue<QueuedWork> Queue = new();
  private static string _autoOpenJobId;
  private static bool _workerRunning;

  public static DownloadJobSnapshot StartOpenFromId(string id, bool openAfterDownload)
  {
    EnsureStorageAvailable();
    string normalizedId = DownloadCachePaths.NormalizeLevelId(id);
    string cacheKey = DownloadCachePaths.BuildTufCacheKey(normalizedId);
    DownloadJob existing = FindActiveByCacheKey(cacheKey);
    if (existing != null) return existing.Snapshot();

    DownloadJob job = Add("level.open-from-id", normalizedId, null, cacheKey, openAfterDownload);
    bool shouldOpen = job.OpenAfterDownload;
    Enqueue(job, () =>
    {
      job.Report("resolving", string.Concat("Resolving TUF level #", normalizedId));
      TufLevelInfo level = TuforumsClient.GetLevelById(normalizedId);
      job.SetResolvedLevel(
        level.Id.ToString(),
        level.DownloadLink,
        level.Song,
        level.Artist,
        FirstNonEmpty(level.Creator, level.Charter, level.Team),
        level.DiffId);
      job.WaitForPresentationObserved(PresentationWaitTimeout);

      LevelOpenUpdateCheckResult updateCheck = LevelUpdateService.CheckForOpen(level.Id, level, job);
      job.RecordUpdateCheck(
        updateCheck.Item,
        updateCheck.State,
        updateCheck.InstalledFileId,
        updateCheck.AvailableFileId,
        updateCheck.AvailableUpdatedAtUtc);

      LevelDownloadResult result = LevelArchiveDownloader.Download(level.DownloadLink, job.CacheKey, job.Token, job.Report);
      DownloadLibraryService.RecordDownload(result, level, normalizedId);
      Complete(job, result, shouldOpen);
    });

    return job.Snapshot();
  }

  public static DownloadJobSnapshot StartOpenFromUrl(string url, bool openAfterDownload)
  {
    EnsureStorageAvailable();
    string cacheKey = BuildUrlCacheKey(url);
    DownloadJob existing = FindActiveByCacheKey(cacheKey);
    if (existing != null) return existing.Snapshot();

    DownloadJob job = Add("level.open-from-url", null, url, cacheKey, openAfterDownload);
    bool shouldOpen = job.OpenAfterDownload;
    Enqueue(job, () =>
    {
      LevelDownloadResult result = LevelArchiveDownloader.Download(url, job.CacheKey, job.Token, job.Report);
      Complete(job, result, shouldOpen);
    });

    return job.Snapshot();
  }

  public static DownloadJobSnapshot StartDownload(string url, string levelId)
  {
    EnsureStorageAvailable();
    string cacheKey = string.IsNullOrWhiteSpace(levelId)
      ? BuildUrlCacheKey(url)
      : DownloadCachePaths.BuildTufCacheKey(levelId);
    DownloadJob existing = FindActiveByCacheKey(cacheKey);
    if (existing != null) return existing.Snapshot();

    DownloadJob job = Add("level.download", levelId, url, cacheKey, false);
    Enqueue(job, () =>
    {
      TufLevelInfo level = null;
      if (DownloadCachePaths.TryParseTufCacheKey(job.CacheKey, out int parsedId))
      {
        try
        {
          level = TuforumsClient.GetLevelMetadataById(parsedId.ToString());
          job.SetLevelInfo(level.Song, level.Artist, FirstNonEmpty(level.Creator, level.Charter, level.Team));
          job.SetDifficultyId(level.DiffId);
        }
        catch (Exception exception)
        {
          Main.Instance?.Warning(string.Concat("Failed to fetch metadata for TUF level #", parsedId, ": ", exception.Message));
        }
      }

      LevelDownloadResult result = LevelArchiveDownloader.Download(url, job.CacheKey, job.Token, job.Report);
      DownloadLibraryService.RecordDownload(result, level, levelId);
      Complete(job, result, false);
    });

    return job.Snapshot();
  }

  public static DownloadJobSnapshot StartUpdateCheck(string id)
  {
    if (LevelUpdateCheckBatchService.IsActive)
      throw new InvalidOperationException("downloaded_level_batch_check_in_progress");
    return StartUpdateCheckCore(id);
  }

  internal static bool TryStartBatchUpdateCheck(string id, out DownloadJobSnapshot snapshot)
  {
    EnsureStorageAvailable();
    string normalizedId = DownloadCachePaths.NormalizeLevelId(id);
    if (!int.TryParse(normalizedId, out int parsedId) || parsedId <= 0)
      throw new ArgumentException("A valid level id is required.", nameof(id));
    string cacheKey = DownloadCachePaths.BuildTufCacheKey(normalizedId);
    DownloadJob job;
    lock (Lock)
    {
      if (Jobs.Values.Any(candidate => candidate.CacheKey == cacheKey && !candidate.IsDone))
      {
        snapshot = null;
        return false;
      }
      job = Add("level.update-check", normalizedId, null, cacheKey, false);
    }
    Enqueue(job, () => LevelUpdateService.Check(parsedId, job));
    snapshot = job.Snapshot();
    return true;
  }

  internal static bool TryStartBatchUpdate(string id, out DownloadJobSnapshot snapshot)
  {
    EnsureStorageAvailable();
    string normalizedId = DownloadCachePaths.NormalizeLevelId(id);
    if (!int.TryParse(normalizedId, out int parsedId) || parsedId <= 0)
      throw new ArgumentException("A valid level id is required.", nameof(id));
    string cacheKey = DownloadCachePaths.BuildTufCacheKey(normalizedId);
    DownloadJob job;
    lock (Lock)
    {
      if (Jobs.Values.Any(candidate => candidate.CacheKey == cacheKey && !candidate.IsDone))
      {
        snapshot = null;
        return false;
      }
      job = Add("level.update", normalizedId, null, cacheKey, false);
    }
    Enqueue(job, () => LevelUpdateService.Update(parsedId, job));
    snapshot = job.Snapshot();
    return true;
  }

  private static DownloadJobSnapshot StartUpdateCheckCore(string id)
  {
    EnsureStorageAvailable();
    string normalizedId = DownloadCachePaths.NormalizeLevelId(id);
    if (!int.TryParse(normalizedId, out int parsedId) || parsedId <= 0)
      throw new ArgumentException("A valid level id is required.", nameof(id));
    string cacheKey = DownloadCachePaths.BuildTufCacheKey(normalizedId);
    DownloadJob existing = FindActiveByCacheKey(cacheKey);
    if (existing != null) return existing.Snapshot();

    DownloadJob job = Add("level.update-check", normalizedId, null, cacheKey, false);
    Enqueue(job, () => LevelUpdateService.Check(parsedId, job));
    return job.Snapshot();
  }

  public static DownloadJobSnapshot StartUpdate(string id)
  {
    if (LevelUpdateCheckBatchService.IsActive)
      throw new InvalidOperationException("downloaded_level_batch_check_in_progress");
    EnsureStorageAvailable();
    string normalizedId = DownloadCachePaths.NormalizeLevelId(id);
    if (!int.TryParse(normalizedId, out int parsedId) || parsedId <= 0)
      throw new ArgumentException("A valid level id is required.", nameof(id));
    string cacheKey = DownloadCachePaths.BuildTufCacheKey(normalizedId);
    DownloadJob existing = FindActiveByCacheKey(cacheKey);
    if (existing != null) return existing.Snapshot();

    DownloadJob job = Add("level.update", normalizedId, null, cacheKey, false);
    Enqueue(job, () => LevelUpdateService.Update(parsedId, job));
    return job.Snapshot();
  }

  public static DownloadJobSnapshot Get(string jobId)
  {
    lock (Lock)
    {
      return Jobs.TryGetValue(jobId ?? "", out DownloadJob job) ? job.Snapshot() : null;
    }
  }

  public static DownloadJobSnapshot[] List()
  {
    lock (Lock)
    {
      PruneTerminalJobs();
      return Jobs.Values
        .OrderBy(job => job.CreatedAtUnixMs)
        .Select(job => job.Snapshot())
        .ToArray();
    }
  }

  public static DownloadJobSnapshot ActiveForModal()
  {
    lock (Lock)
    {
      return Jobs.Values
        .Where(ShouldDisplayInGame)
        .Select(job => job.Snapshot())
        .Where(snapshot => !snapshot.Done || IsUndismissedDiskSpaceFailure(snapshot))
        .OrderBy(snapshot => IsUndismissedDiskSpaceFailure(snapshot) ? 0 : snapshot.Status == "waiting_selection" ? 1 : snapshot.Status == "running" ? 2 : 3)
        .ThenBy(snapshot => snapshot.CreatedAtUnixMs)
        .FirstOrDefault();
    }
  }

  internal static bool ShouldDisplayInGame(DownloadJobSnapshot snapshot)
  {
    return snapshot != null && snapshot.Kind is not "level.update" and not "level.update-check";
  }

  internal static bool ShouldDisplayInGame(DownloadJob job)
  {
    if (job == null) return false;
    DownloadJobSnapshot snapshot = job.Snapshot();
    return ShouldDisplayInGame(snapshot)
      && (!string.Equals(snapshot.Kind, "level.open-from-id", StringComparison.Ordinal)
        || job.HasPresentationInfo);
  }

  internal static void MarkPresentationShown(string jobId)
  {
    lock (Lock)
    {
      if (Jobs.TryGetValue(jobId ?? string.Empty, out DownloadJob job))
        job.MarkPresentationObserved();
    }
  }

  internal static bool DismissModal(string jobId)
  {
    lock (Lock)
    {
      if (!Jobs.TryGetValue(jobId ?? "", out DownloadJob job)) return false;
      DownloadJobSnapshot snapshot = job.Snapshot();
      if (!IsDiskSpaceFailure(snapshot)) return false;

      DismissedModalJobIds.Add(job.JobId);
      return true;
    }
  }

  public static bool Cancel(string jobId)
  {
    DownloadJob job;

    lock (Lock)
    {
      if (!Jobs.TryGetValue(jobId ?? "", out job)) return false;
      if (!job.TryCancel()) return false;
      RecalculateQueuePositions();
    }

    ReleaseAutoOpen(job);
    PruneTerminalJobs();
    return true;
  }

  public static bool SelectLevel(string jobId, string levelPath)
  {
    EnsureStorageAvailable();
    DownloadJob job;

    lock (Lock)
    {
      if (!Jobs.TryGetValue(jobId ?? "", out job)) return false;
    }

    if (!job.SelectLevel(levelPath)) return false;
    QueueUpdateWarningIfNeeded(job);
    LevelOpenService.Open(levelPath);
    ReleaseAutoOpen(job);
    PruneTerminalJobs();
    return true;
  }

  private static DownloadJob Add(
    string kind,
    string levelId,
    string sourceUrl,
    string cacheKey,
    bool requestAutoOpen)
  {
    lock (Lock)
    {
      bool shouldOpen = requestAutoOpen && _autoOpenJobId == null;
      DownloadJob job = new(kind, levelId, sourceUrl, cacheKey, shouldOpen);
      Jobs[job.JobId] = job;
      if (shouldOpen)
      {
        _autoOpenJobId = job.JobId;
      }

      return job;
    }
  }

  public static bool HasActiveJobs()
  {
    lock (Lock) return Jobs.Values.Any(job => !job.IsDone);
  }

  private static void EnsureStorageAvailable()
  {
    if (DownloadStorageMigrationService.IsMigrationActive)
      throw new InvalidOperationException("storage_migration_in_progress");
  }

  private static void Enqueue(DownloadJob job, Action action)
  {
    lock (Lock)
    {
      Queue.Enqueue(new QueuedWork(job, action));
      RecalculateQueuePositions();
      if (_workerRunning) return;

      _workerRunning = true;
    }

    Task.Run(RunWorker);
  }

  private static void RunWorker()
  {
    while (true)
    {
      QueuedWork work;

      lock (Lock)
      {
        work = NextQueuedWork();
        if (work == null)
        {
          _workerRunning = false;
          return;
        }

        RecalculateQueuePositions();
      }

      RunQueued(work);
    }
  }

  private static QueuedWork NextQueuedWork()
  {
    while (Queue.Count > 0)
    {
      QueuedWork work = Queue.Dequeue();
      if (work.Job.IsQueued && !work.Job.Token.IsCancellationRequested)
      {
        work.Job.SetQueuePosition(0);
        return work;
      }

      ReleaseAutoOpen(work.Job);
    }

    return null;
  }

  private static void RunQueued(QueuedWork work)
  {
    DownloadJob job = work.Job;

    try
    {
      if (job.Token.IsCancellationRequested)
      {
        ReleaseAutoOpen(job);
        return;
      }

      job.BeginRunning();
      work.Action();
    }
    catch (OperationCanceledException)
    {
      job.Cancel();
      ReleaseAutoOpen(job);
    }
    catch (Exception e)
    {
      job.Fail(e);
      ReleaseAutoOpen(job);
      Main.Instance?.LogException(e);
    }
    finally
    {
      lock (Lock)
      {
        RecalculateQueuePositions();
        PruneTerminalJobs();
      }
    }
  }

  private static DownloadJob FindActiveByCacheKey(string cacheKey)
  {
    lock (Lock)
    {
      return Jobs.Values.FirstOrDefault(job => job.CacheKey == cacheKey && !job.IsDone);
    }
  }

  private static void RecalculateQueuePositions()
  {
    int position = 1;
    foreach (QueuedWork work in Queue)
    {
      DownloadJob job = work.Job;
      if (!job.IsQueued || job.Token.IsCancellationRequested)
      {
        job.SetQueuePosition(-1);
        continue;
      }

      job.SetQueuePosition(position++);
    }
  }

  private static void PruneTerminalJobs()
  {
    lock (Lock)
    {
      string[] expiredJobIds = Jobs.Values
        .Where(job => job.IsDone)
        .OrderByDescending(job => job.CreatedAtUnixMs)
        .Skip(MaxRetainedTerminalJobs)
        .Select(job => job.JobId)
        .ToArray();

      foreach (string jobId in expiredJobIds)
      {
        Jobs.Remove(jobId);
        DismissedModalJobIds.Remove(jobId);
      }
    }
  }

  private static bool IsUndismissedDiskSpaceFailure(DownloadJobSnapshot snapshot)
  {
    return IsDiskSpaceFailure(snapshot) && !DismissedModalJobIds.Contains(snapshot.JobId);
  }

  private static bool IsDiskSpaceFailure(DownloadJobSnapshot snapshot)
  {
    return string.Equals(snapshot?.ErrorCode, "insufficient_disk_space", StringComparison.Ordinal);
  }

  private static void Complete(DownloadJob job, LevelDownloadResult result, bool openAfterDownload)
  {
    if (result.LevelPaths.Count == 0)
    {
      job.Fail(new FileNotFoundException("No .adofai file was found in the downloaded archive.", result.Directory));
      ReleaseAutoOpen(job);
      return;
    }

    bool opened = false;

    if (openAfterDownload)
    {
      if (result.LevelPaths.Count > 1)
      {
        job.WaitForSelection(result);
        return;
      }

      job.Report("opening", "Opening level", 1);
      QueueUpdateWarningIfNeeded(job);
      LevelOpenService.Open(result.SelectedLevelPath);
      opened = true;
    }

    job.Complete(result, opened);
    ReleaseAutoOpen(job);
  }

  private static string BuildUrlCacheKey(string url)
  {
    using SHA256 sha256 = SHA256.Create();
    byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(url ?? ""));
    return string.Concat("url-", BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant());
  }

  private static string FirstNonEmpty(params string[] values)
  {
    foreach (string value in values)
    {
      if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
    }

    return null;
  }

  private static void ReleaseAutoOpen(DownloadJob job)
  {
    lock (Lock)
    {
      if (_autoOpenJobId == job.JobId)
      {
        _autoOpenJobId = null;
      }
    }
  }

  private static void QueueUpdateWarningIfNeeded(DownloadJob job)
  {
    if (!string.Equals(job?.Snapshot().UpdateState, LevelOpenUpdateCheckResult.UpdateAvailable, StringComparison.Ordinal))
      return;

    DownloadStatusOverlay.QueueUpdateWarning(new UpdateWarningToastModel());
  }
}
