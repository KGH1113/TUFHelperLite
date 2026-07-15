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
    string normalizedId = DownloadCachePaths.NormalizeLevelId(id);
    string cacheKey = DownloadCachePaths.BuildTufCacheKey(normalizedId);
    DownloadJob existing = FindActiveByCacheKey(cacheKey);
    if (existing != null) return existing.Snapshot();

    DownloadJob job = Add("level.open-from-id", normalizedId, null, cacheKey, openAfterDownload);
    bool shouldOpen = job.OpenAfterDownload;
    Enqueue(job, () =>
    {
      job.Report("resolving", $"Resolving TUF level #{normalizedId}");
      TufLevelInfo level = TuforumsClient.GetLevelById(normalizedId);
      job.SetLevel(level.Id.ToString(), level.DownloadLink);
      job.SetLevelInfo(level.Song, level.Artist, FirstNonEmpty(level.Creator, level.Charter, level.Team));
      job.SetDifficultyId(level.DiffId);

      LevelDownloadResult result = LevelArchiveDownloader.Download(level.DownloadLink, job.CacheKey, job.Token, job.Report);
      Complete(job, result, shouldOpen);
    });

    return job.Snapshot();
  }

  public static DownloadJobSnapshot StartOpenFromUrl(string url, bool openAfterDownload)
  {
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
    string cacheKey = string.IsNullOrWhiteSpace(levelId)
      ? BuildUrlCacheKey(url)
      : DownloadCachePaths.BuildTufCacheKey(levelId);
    DownloadJob existing = FindActiveByCacheKey(cacheKey);
    if (existing != null) return existing.Snapshot();

    DownloadJob job = Add("level.download", levelId, url, cacheKey, false);
    Enqueue(job, () =>
    {
        LevelDownloadResult result = LevelArchiveDownloader.Download(url, job.CacheKey, job.Token, job.Report);
      Complete(job, result, false);
    });

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
        .Select(job => job.Snapshot())
        .Where(snapshot => !snapshot.Done || IsUndismissedDiskSpaceFailure(snapshot))
        .OrderBy(snapshot => IsUndismissedDiskSpaceFailure(snapshot) ? 0 : snapshot.Status == "waiting_selection" ? 1 : snapshot.Status == "running" ? 2 : 3)
        .ThenBy(snapshot => snapshot.CreatedAtUnixMs)
        .FirstOrDefault();
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
      job.Cancel();
      RecalculateQueuePositions();
    }

    ReleaseAutoOpen(job);
    PruneTerminalJobs();
    return true;
  }

  public static bool SelectLevel(string jobId, string levelPath)
  {
    DownloadJob job;

    lock (Lock)
    {
      if (!Jobs.TryGetValue(jobId ?? "", out job)) return false;
    }

    if (!job.SelectLevel(levelPath)) return false;
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
    return $"url-{BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()}";
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
}
