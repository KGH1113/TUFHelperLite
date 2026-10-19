using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TUFHelperLite.App;
using TUFHelperLite.Domain.Errors;
using TUFHelperLite.Domain.Levels;
using TUFHelperLite.Infrastructure.Downloads;
using TUFHelperLite.Infrastructure.Settings;

internal static partial class Program
{
  private static void RunDownloadSettingsTests()
  {
    string install = Path.Combine(Path.GetTempPath(), "tufhelper-settings-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(install);
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    try
    {
      DownloadStorageSettingsStore.Initialize(install);
      DownloadStorageMigrationService.Initialize(install);
      DownloadStorageMigrationService.SetLevelInUseProbeForTests(_ => false);
      LevelUpdateCheckBatchService.Initialize(install);
      string persistentRoot = Path.Combine(install, "custom-library");
      Directory.CreateDirectory(persistentRoot);
      DownloadStorageSettingsStore.SetDownloadRoot(persistentRoot);
      DownloadLibraryService.Initialize(install);
      DownloadLibraryService.RebuildSummaryForTests();
      DownloadSettingsService.SetTempDownloadMode(true);
      DownloadStorageSettingsStore.Initialize(install);
      CheckTrue("temporary mode persists after settings reload", DownloadSettingsService.GetStatus().TempDownloadMode);
      CheckString("temporary mode preserves configured library", persistentRoot, DownloadCachePaths.GetDownloadRoot());
      string temporary = Path.Combine(install, "Downloads", "temp");
      CheckString("temporary directory is fixed to the mod install", temporary, DownloadSettingsService.GetStatus().TempDirectory);

      TempLevelDownloader.SetDownloaderForTests((url, staging, token, _) => {
        string file = CreateLevel(staging, "", "level.adofai");
        File.WriteAllText(file, url);
        return new LevelDownloadResult { Directory = staging, SelectedLevelPath = file, LevelPaths = new() { file } };
      });
      LevelDownloadResult first = LevelArchiveDownloader.Download("first", "tuf-123", CancellationToken.None);
      CheckString("temporary extraction uses the directory itself", temporary, first.Directory);
      CheckTrue("temporary extraction does not create a level subdirectory", !Directory.Exists(Path.Combine(temporary, "tuf-123")));
      CheckString("temporary selected level points at the active root", Path.Combine(temporary, "level.adofai"), first.SelectedLevelPath);
      File.WriteAllText(Path.Combine(temporary, "old-only.txt"), "old");
      LevelArchiveDownloader.Download("second", "tuf-456", CancellationToken.None);
      CheckString("new temporary level replaces the previous payload", "second", File.ReadAllText(Path.Combine(temporary, "level.adofai")));
      CheckFalse("temporary replacement removes previous exclusive files", File.Exists(Path.Combine(temporary, "old-only.txt")));
      DownloadLibraryService.RecordDownload(first, null, "123");
      CheckLong("temporary download is excluded from saved library count", 0, DownloadLibraryService.GetSummary().LevelCount);
      CheckFalse("temporary download has no persistent library manifest", File.Exists(Path.Combine(temporary, DownloadLibraryService.ManifestFileName)));
      ExpectSettingsError("temp mode rejects library page", "temp_download_mode_enabled", () => DownloadLibraryService.GetPage(null, "next", 20));
      ExpectSettingsError("temp mode rejects persistent deletion", "temp_download_mode_enabled", () => DownloadSettingsService.DeleteLevel("123"));
      ExpectSettingsError("temp mode rejects native storage picker", "temp_download_mode_enabled", () => DownloadFolderPickerCoordinator.Start());
      ExpectSettingsError("temp mode rejects persistent update batch", "temp_download_mode_enabled", () => LevelUpdateCheckBatchService.Start());
      ExpectSettingsError("temp mode rejects storage changes", "temp_download_mode_enabled", () => DownloadStorageMigrationService.StartForTarget(Path.Combine(install, "next"), true));

      TempLevelDownloader.SetDownloaderForTests((_, staging, token, _) => {
        CreateLevel(staging, "", "partial.adofai");
        throw new IOException("fixture download failed");
      });
      try { LevelArchiveDownloader.Download("failure", "tuf-789", CancellationToken.None); }
      catch (IOException) { }
      CheckString("failed temporary candidate keeps previous level", "second", File.ReadAllText(Path.Combine(temporary, "level.adofai")));

      TempLevelDownloader.SetDownloaderForTests((_, staging, token, _) => {
        CreateLevel(staging, "", "cancelled.adofai");
        entered.Set(); release.Wait(TimeSpan.FromSeconds(10));
        token.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Cancellation was not observed.");
      });
      var job = LevelJobService.StartOpenFromUrl("cancelled-temp", false);
      CheckTrue("temporary job reached staged download", entered.Wait(TimeSpan.FromSeconds(5)));
      ExpectSettingsError("active download rejects flush", "download_jobs_active", () => DownloadSettingsService.FlushTemp());
      LevelJobService.Cancel(job.JobId);
      ExpectSettingsError("cancelled worker blocks mode changes until cleanup", "download_jobs_active", () => DownloadSettingsService.SetTempDownloadMode(false));
      release.Set();
      CheckTrue("cancelled temporary worker finishes cleanup", SpinWait.SpinUntil(() => !LevelJobService.HasPendingWork(), 5000));
      CheckString("cancelled temporary candidate keeps previous level", "second", File.ReadAllText(Path.Combine(temporary, "level.adofai")));
      CheckFalse("temporary work directories are cleaned after cancellation", Directory.Exists(Path.Combine(install, "Downloads", ".tufhelperlite-temp-work")));

      TempLevelDownloader.SetDownloaderForTests((_, staging, token, _) => {
        string left = CreateLevel(staging, "", "left.adofai");
        string right = CreateLevel(staging, "", "right.adofai");
        return new LevelDownloadResult { Directory = staging, SelectedLevelPath = left, LevelPaths = new() { left, right } };
      });
      var selectionJob = LevelJobService.StartOpenFromUrl("temp-selection", true);
      CheckTrue("temporary multi-chart job waits for selection", SpinWait.SpinUntil(() => LevelJobService.Get(selectionJob.JobId).WaitingForSelection, 5000));
      var queuedNext = LevelJobService.StartOpenFromUrl("temp-after-selection", false);
      CheckTrue("next temporary job fails while a selection owns the payload", SpinWait.SpinUntil(() => LevelJobService.Get(queuedNext.JobId).Done, 5000));
      CheckString("selection reservation has a specific job error", "download_jobs_active", LevelJobService.Get(queuedNext.JobId).ErrorCode);
      CheckTrue("pending selection files survive the next job", File.Exists(Path.Combine(temporary, "left.adofai")) && File.Exists(Path.Combine(temporary, "right.adofai")));
      LevelJobService.Cancel(selectionJob.JobId);
      CheckTrue("selection cancellation releases temporary work", SpinWait.SpinUntil(() => !LevelJobService.HasPendingWork(), 5000));

      PendingLevelOpen.Reserve(Path.Combine(temporary, "left.adofai"));
      ExpectSettingsError("queued game-thread open reserves temporary files", "downloaded_level_in_use", () => DownloadSettingsService.FlushTemp());
      PendingLevelOpen.Reserve(null);
      PendingLevelOpen.Reserve(Path.Combine(temporary, "queued-one.adofai"));
      PendingLevelOpen.Reserve(Path.Combine(persistentRoot, "tuf-123", "queued-two.adofai"));
      PendingLevelOpen.Release(Path.Combine(temporary, "queued-one.adofai"));
      CheckTrue("observing one queued open keeps other reservations", PendingLevelOpen.IsInside(persistentRoot));
      PendingLevelOpen.Reserve(null);

      TempLevelDownloader.SetDownloaderForTests((_, staging, token, _) => {
        string next = CreateLevel(staging, "", "next.adofai");
        return new LevelDownloadResult { Directory = staging, SelectedLevelPath = next, LevelPaths = new() { next } };
      });
      var cancelledRelease = new ControlledTempRelease();
      TempLevelDownloader.LevelRelease = cancelledRelease;
      DownloadStorageMigrationService.SetLevelInUseProbeForTests(_ => true);
      var releaseJob = LevelJobService.StartOpenFromUrl("waiting-release-cancel", false);
      CheckTrue("next temporary level waits for game release after staging", cancelledRelease.Started.Task.Wait(5000));
      CheckTrue("live temporary payload stays intact during release wait", File.Exists(Path.Combine(temporary, "left.adofai")));
      CheckString("waiting release reports actionable progress", "waiting_for_level_close", LevelJobService.Get(releaseJob.JobId).Stage);
      CheckFalse("waiting release lets native unsaved-changes UI receive input", LevelJobService.ShouldDisplayInGame(LevelJobService.Get(releaseJob.JobId)));
      ExpectSettingsError("release wait prevents mode changes", "download_jobs_active", () => DownloadSettingsService.SetTempDownloadMode(false));
      LevelJobService.Cancel(releaseJob.JobId);
      CheckTrue("cancel releases the game transition wait", SpinWait.SpinUntil(() => !LevelJobService.HasPendingWork(), 5000));
      CheckTrue("cancelled game-release wait leaves old temporary payload", File.Exists(Path.Combine(temporary, "left.adofai")));
      CheckFalse("cancelled game-release wait never activates candidate", File.Exists(Path.Combine(temporary, "next.adofai")));

      var successfulRelease = new ControlledTempRelease();
      TempLevelDownloader.LevelRelease = successfulRelease;
      var replacementJob = LevelJobService.StartOpenFromUrl("waiting-release-success", false);
      CheckTrue("following temporary download requests a fresh release", successfulRelease.Started.Task.Wait(5000));
      DownloadStorageMigrationService.SetLevelInUseProbeForTests(_ => false);
      successfulRelease.Completed.TrySetResult();
      CheckTrue("temporary download activates only after game release", SpinWait.SpinUntil(() => !LevelJobService.HasPendingWork(), 5000));
      CheckTrue("released temporary download replaces old root payload", File.Exists(Path.Combine(temporary, "next.adofai")) && !File.Exists(Path.Combine(temporary, "left.adofai")));
      CheckString("released temporary job completes normally", "completed", LevelJobService.Get(replacementJob.JobId).Status);
      TempLevelDownloader.LevelRelease = null;

      DownloadStorageMigrationService.SetLevelInUseProbeForTests(_ => true);
      ExpectSettingsError("open temporary level cannot be flushed", "downloaded_level_in_use", () => DownloadSettingsService.FlushTemp());
      DownloadStorageMigrationService.SetLevelInUseProbeForTests(_ => false);
      DownloadSettingsService.FlushTemp();
      CheckTrue("flush retains an empty temp directory", Directory.Exists(temporary) && !Directory.EnumerateFileSystemEntries(temporary).Any());

      DownloadSettingsService.SetTempDownloadMode(false);
      string downloaded = CreateLevel(persistentRoot, "tuf-123", "level.adofai");
      DownloadLibraryService.RecordDownload(new LevelDownloadResult { Directory = Path.GetDirectoryName(downloaded), LevelPaths = new() { downloaded } }, null, "123");
      DownloadLibraryService.RebuildSummaryForTests();
      string outside = CreateLevel(install, "outside", "keep.adofai");
      ExpectSettingsError("delete rejects path traversal ID", "invalid_level_id", () => DownloadSettingsService.DeleteLevel("../outside"));
      DownloadStorageMigrationService.SetLevelInUseProbeForTests(_ => true);
      ExpectSettingsError("currently opened saved level cannot be deleted", "downloaded_level_in_use", () => DownloadSettingsService.DeleteLevel("123"));
      DownloadStorageMigrationService.SetLevelInUseProbeForTests(_ => false);
      string linked = Path.Combine(persistentRoot, "tuf-123", "linked");
      Directory.CreateSymbolicLink(linked, Path.GetDirectoryName(outside));
      ExpectSettingsError("deletion rejects nested filesystem links", "download_path_invalid", () => DownloadSettingsService.DeleteLevel("123"));
      Directory.Delete(linked);
      CheckTrue("saved level deletion reports removal", DownloadSettingsService.DeleteLevel("123"));
      CheckFalse("saved level directory is deleted", Directory.Exists(Path.Combine(persistentRoot, "tuf-123")));
      CheckTrue("deletion never touches unrelated directories", File.Exists(outside));
      CheckFalse("repeated deletion is a harmless no-op", DownloadSettingsService.DeleteLevel("123"));
      CheckTrue("delete rebuilds library summary", SpinWait.SpinUntil(() => DownloadLibraryService.GetSummary().State == "ready", 5000));
      CheckLong("delete updates saved library count", 0, DownloadLibraryService.GetSummary().LevelCount);

      CreateLevel(DownloadStorageSettingsStore.GetDefaultRoot(), "temp", "retained.adofai");
      DownloadStorageSettingsStore.SetDownloadRoot(DownloadStorageSettingsStore.GetDefaultRoot());
      string saved = CreateLevel(DownloadCachePaths.GetDownloadRoot(), "tuf-456", "level.adofai");
      string migrated = Path.Combine(install, "migrated-library");
      DownloadStorageMigrationService.StartForTarget(migrated, true);
      CheckTrue("migration with retained temporary files completes", DownloadStorageMigrationService.WaitForWorkerForTests());
      CheckString("persistent storage migration completed", "completed", DownloadStorageMigrationService.GetStatus().State);
      CheckTrue("migration moves saved levels", File.Exists(Path.Combine(migrated, "tuf-456", "level.adofai")));
      CheckTrue("migration keeps temp at the install path", File.Exists(Path.Combine(temporary, "retained.adofai")));
      CheckFalse("migration excludes temporary files from the saved library", Directory.Exists(Path.Combine(migrated, "temp")));
      string selection = DownloadStorageMigrationService.ValidateChangeTarget(DownloadStorageSettingsStore.GetDefaultRoot(), out string kind);
      CheckString("default storage can be selected with only temp remaining", "migration", kind);
      try { DownloadStorageMigrationService.ValidateChangeTarget(temporary, out _); CheckTrue("temporary directory cannot become saved storage", false); }
      catch (DownloadStorageMigrationException error) { CheckString("temporary directory cannot become saved storage", "storage_target_reserved", error.Code); }
    }
    finally
    {
      release.Set();
      SpinWait.SpinUntil(() => !LevelJobService.HasPendingWork(), 5000);
      TempLevelDownloader.SetDownloaderForTests(null);
      TempLevelDownloader.LevelRelease = null;
      PendingLevelOpen.Reserve(null);
      DownloadStorageMigrationService.WaitForWorkerForTests();
      DownloadStorageMigrationService.SetLevelInUseProbeForTests(_ => false);
      if (Directory.Exists(install)) Directory.Delete(install, true);
    }
  }

  private static void ExpectSettingsError(string label, string code, Action action)
  {
    try { action(); CheckTrue(label, false); }
    catch (LevelUpdateException exception) { CheckString(label, code, exception.Code); }
  }

  private sealed class ControlledTempRelease : TUFHelperLite.App.Ports.ITempLevelRelease
  {
    internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task ReleaseAsync(string directory, CancellationToken cancellationToken)
    {
      Started.TrySetResult();
      await Completed.Task.WaitAsync(cancellationToken);
    }
  }
}
