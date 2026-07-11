using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TUFHelperLite.Domain.Jobs;
using TUFHelperLite.Domain.Levels;
using TUFHelperLite.Infrastructure.Downloads;
using TUFHelperLite.Infrastructure.Tuforums;
using TUFHelperLite.Presentation.Unity;

namespace TUFHelperLite.App;

public static class LevelJobService
{
  private static readonly object Lock = new();
  private static readonly Dictionary<string, DownloadJob> Jobs = new();
  private static readonly Queue<DownloadJob> Queue = new();
  private static readonly SemaphoreSlim Slots = new(1, 1);
  private static string _autoOpenJobId;

  public static DownloadJobSnapshot StartOpenFromId(string id, bool openAfterDownload)
  {
    string normalizedId = NormalizeLevelId(id);
    string cacheKey = $"tuf-{normalizedId}";
    DownloadJob existing = FindActiveByCacheKey(cacheKey);
    if (existing != null) return existing.Snapshot();

    bool shouldOpen = ReserveAutoOpen(openAfterDownload);
    DownloadJob job = Add(new DownloadJob("level.open-from-id", normalizedId, null, cacheKey, shouldOpen));
    AssignAutoOpenJob(job, shouldOpen);
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

    bool shouldOpen = ReserveAutoOpen(openAfterDownload);
    DownloadJob job = Add(new DownloadJob("level.open-from-url", null, url, cacheKey, shouldOpen));
    AssignAutoOpenJob(job, shouldOpen);
    Enqueue(job, () =>
    {
      LevelDownloadResult result = LevelArchiveDownloader.Download(url, job.CacheKey, job.Token, job.Report);
      Complete(job, result, shouldOpen);
    });

    return job.Snapshot();
  }

  public static DownloadJobSnapshot StartDownload(string url, string levelId)
  {
    string cacheKey = string.IsNullOrWhiteSpace(levelId) ? BuildUrlCacheKey(url) : $"tuf-{NormalizeLevelId(levelId)}";
    DownloadJob existing = FindActiveByCacheKey(cacheKey);
    if (existing != null) return existing.Snapshot();

    DownloadJob job = Add(new DownloadJob("level.download", levelId, url, cacheKey, false));
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
        .Where(snapshot => !snapshot.Done)
        .OrderBy(snapshot => snapshot.Status == "waiting_selection" ? 0 : snapshot.Status == "running" ? 1 : 2)
        .ThenBy(snapshot => snapshot.CreatedAtUnixMs)
        .FirstOrDefault();
    }
  }

  public static bool Cancel(string jobId)
  {
    lock (Lock)
    {
      if (!Jobs.TryGetValue(jobId ?? "", out DownloadJob job)) return false;
      job.Cancel();
      RecalculateQueuePositions();
      return true;
    }
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
    return true;
  }

  private static DownloadJob Add(DownloadJob job)
  {
    lock (Lock)
    {
      Jobs[job.JobId] = job;
      return job;
    }
  }

  private static void Enqueue(DownloadJob job, Action action)
  {
    lock (Lock)
    {
      Queue.Enqueue(job);
      RecalculateQueuePositions();
    }

    Task.Run(() => RunQueued(job, action));
  }

  private static async Task RunQueued(DownloadJob job, Action action)
  {
    await Slots.WaitAsync();

    try
    {
      if (job.Token.IsCancellationRequested) return;

      lock (Lock)
      {
        RemoveFromQueue(job);
        RecalculateQueuePositions();
      }

      job.BeginRunning();
      action();
    }
    catch (OperationCanceledException)
    {
      job.Cancel();
    }
    catch (Exception e)
    {
      job.Fail(e);
      Main.Instance?.LogException(e);
    }
    finally
    {
      Slots.Release();
      lock (Lock)
      {
        RecalculateQueuePositions();
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

  private static void RemoveFromQueue(DownloadJob target)
  {
    int count = Queue.Count;

    for (int i = 0; i < count; i++)
    {
      DownloadJob job = Queue.Dequeue();
      if (job.JobId != target.JobId && job.IsQueued && !job.Token.IsCancellationRequested)
      {
        Queue.Enqueue(job);
      }
    }
  }

  private static void RecalculateQueuePositions()
  {
    int position = 1;
    foreach (DownloadJob job in Queue)
    {
      if (!job.IsQueued || job.Token.IsCancellationRequested)
      {
        job.SetQueuePosition(-1);
        continue;
      }

      job.SetQueuePosition(position++);
    }
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

  private static string NormalizeLevelId(string id)
  {
    return (id ?? "").Trim().TrimStart('#');
  }

  private static string BuildUrlCacheKey(string url)
  {
    return $"url-{(url ?? "").GetHashCode()}";
  }

  private static string FirstNonEmpty(params string[] values)
  {
    foreach (string value in values)
    {
      if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
    }

    return null;
  }

  private static bool ReserveAutoOpen(bool requested)
  {
    if (!requested) return false;

    lock (Lock)
    {
      if (_autoOpenJobId != null) return false;
      return true;
    }
  }

  private static void AssignAutoOpenJob(DownloadJob job, bool shouldOpen)
  {
    if (!shouldOpen) return;

    lock (Lock)
    {
      _autoOpenJobId = job.JobId;
    }
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
