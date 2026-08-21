using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TUFHelperLite.Domain.Jobs;
using TUFHelperLite.Domain.Levels;
using TUFHelperLite.Infrastructure.Downloads;

namespace TUFHelperLite.App;

public static class LevelUpdateCheckBatchService
{
  private static readonly object Gate = new();
  private static string _snapshotPath;
  private static LevelUpdateCheckBatchSnapshot _snapshot = new();
  private static CancellationTokenSource _cancellation;
  private static string _ownedJobId;

  public static bool IsActive
  {
    get { lock (Gate) return _snapshot.State is "preparing" or "checking" or "cancelling"; }
  }

  public static void Initialize(string installPath)
  {
    _snapshotPath = Path.Combine(installPath ?? AppDomain.CurrentDomain.BaseDirectory, "DownloadUpdateCheckBatch.ids");
    TryDeleteSnapshot();
    lock (Gate) _snapshot = Idle();
  }

  public static LevelUpdateCheckBatchSnapshot Start()
  {
    lock (Gate)
    {
      if (IsActiveState(_snapshot.State)) return Clone(_snapshot);
      if (DownloadStorageMigrationService.IsMigrationActive)
        return Failure("storage_migration_in_progress", "Wait for the storage operation to finish.");
      _snapshot = new LevelUpdateCheckBatchSnapshot
      {
        State = "preparing",
        OperationId = Guid.NewGuid().ToString("N"),
        Message = "Preparing downloaded levels."
      };
      _cancellation = new CancellationTokenSource();
      _ownedJobId = null;
      Task.Run(() => Run(_snapshot.OperationId, _cancellation.Token));
      return Clone(_snapshot);
    }
  }

  public static LevelUpdateCheckBatchSnapshot GetStatus()
  {
    lock (Gate) return Clone(_snapshot);
  }

  public static LevelUpdateCheckBatchSnapshot Cancel()
  {
    lock (Gate)
    {
      if (!IsActiveState(_snapshot.State)) return Clone(_snapshot);
      _snapshot.State = "cancelling";
      _snapshot.Message = "Cancelling update check.";
      _cancellation?.Cancel();
      if (!string.IsNullOrWhiteSpace(_ownedJobId)) LevelJobService.Cancel(_ownedJobId);
      return Clone(_snapshot);
    }
  }

  private static void Run(string operationId, CancellationToken token)
  {
    try
    {
      int total = DownloadLibraryService.WriteDownloadedIdSnapshot(_snapshotPath, token);
      Update(snapshot =>
      {
        snapshot.State = "checking";
        snapshot.LevelsTotal = total;
        snapshot.Message = total == 0 ? "There are no downloaded levels to check." : "Checking downloaded levels.";
      });
      if (total == 0)
      {
        Complete("completed", "All downloaded levels are up to date.");
        return;
      }

      using StreamReader reader = new(_snapshotPath);
      string line;
      while ((line = reader.ReadLine()) != null)
      {
        token.ThrowIfCancellationRequested();
        if (!int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id <= 0) continue;
        ProcessLevel(id, token);
      }

      LevelUpdateCheckBatchSnapshot result = GetStatus();
      Complete("completed", result.UpdatesAvailable > 0
        ? $"{result.UpdatesAvailable} updates are available."
        : "All downloaded levels are up to date.");
    }
    catch (OperationCanceledException)
    {
      Complete("cancelled", "Update check cancelled.");
    }
    catch (Exception exception)
    {
      Complete("failed", exception.Message, exception is DownloadStorageMigrationException migration
        ? migration.Code
        : "downloaded_level_batch_check_failed");
      Main.Instance?.LogException(exception);
    }
    finally
    {
      lock (Gate)
      {
        _ownedJobId = null;
        _cancellation?.Dispose();
        _cancellation = null;
      }
      TryDeleteSnapshot();
    }
  }

  private static void ProcessLevel(int id, CancellationToken token)
  {
    DownloadJobSnapshot job = null;
    while (job == null)
    {
      token.ThrowIfCancellationRequested();
      if (DownloadStorageMigrationService.IsMigrationActive)
        throw new DownloadStorageMigrationException("storage_migration_in_progress", "The storage root changed during the update check.");
      if (!LevelJobService.TryStartBatchUpdateCheck(id.ToString(CultureInfo.InvariantCulture), out job))
      {
        Thread.Sleep(50);
      }
    }

    lock (Gate) _ownedJobId = job.JobId;
    Update(snapshot =>
    {
      snapshot.CurrentLevelId = id.ToString(CultureInfo.InvariantCulture);
      snapshot.CurrentStage = job.Stage;
      snapshot.CurrentProgress = job.Progress;
    });

    while (!job.Done)
    {
      token.ThrowIfCancellationRequested();
      Thread.Sleep(100);
      job = LevelJobService.Get(job.JobId);
      if (job == null) throw new InvalidOperationException("The update-check job disappeared.");
      DownloadJobSnapshot current = job;
      Update(snapshot =>
      {
        snapshot.CurrentStage = current.Stage;
        snapshot.CurrentProgress = current.Progress;
      });
    }

    lock (Gate) _ownedJobId = null;
    Update(snapshot =>
    {
      snapshot.LevelsProcessed++;
      if (job.Status == "completed" && job.UpdateState == "update_available") snapshot.UpdatesAvailable++;
      else if (job.Status == "completed") snapshot.LevelsUpToDate++;
      else snapshot.LevelsFailed++;
      snapshot.CurrentStage = job.Stage;
      snapshot.CurrentProgress = 1;
    });
  }

  private static void Complete(string state, string message, string errorCode = null)
  {
    Update(snapshot =>
    {
      snapshot.State = state;
      snapshot.CurrentLevelId = null;
      snapshot.CurrentStage = null;
      snapshot.CurrentProgress = 0;
      snapshot.ErrorCode = errorCode;
      snapshot.Message = message;
    });
  }

  private static void Update(Action<LevelUpdateCheckBatchSnapshot> action)
  {
    lock (Gate) action(_snapshot);
  }

  private static bool IsActiveState(string state) => state is "preparing" or "checking" or "cancelling";

  private static LevelUpdateCheckBatchSnapshot Idle() => new()
  {
    State = "idle",
    Message = "Ready to check downloaded levels."
  };

  private static LevelUpdateCheckBatchSnapshot Failure(string code, string message) => new()
  {
    State = "failed",
    ErrorCode = code,
    Message = message
  };

  private static LevelUpdateCheckBatchSnapshot Clone(LevelUpdateCheckBatchSnapshot value) => new()
  {
    State = value.State,
    OperationId = value.OperationId,
    CurrentLevelId = value.CurrentLevelId,
    CurrentStage = value.CurrentStage,
    CurrentProgress = value.CurrentProgress,
    LevelsProcessed = value.LevelsProcessed,
    LevelsTotal = value.LevelsTotal,
    UpdatesAvailable = value.UpdatesAvailable,
    LevelsUpToDate = value.LevelsUpToDate,
    LevelsFailed = value.LevelsFailed,
    ErrorCode = value.ErrorCode,
    Message = value.Message
  };

  private static void TryDeleteSnapshot()
  {
    try { if (!string.IsNullOrWhiteSpace(_snapshotPath) && File.Exists(_snapshotPath)) File.Delete(_snapshotPath); }
    catch { }
  }
}
