using System;
using System.Globalization;
using System.IO;
using TUFHelperLite.Domain.Errors;
using TUFHelperLite.Domain.Ports;
using TUFHelperLite.Infrastructure.Downloads;
using TUFHelperLite.Infrastructure.Settings;

namespace TUFHelperLite.App;

public sealed class DownloadSettingsSnapshot
{
  public bool TempDownloadMode { get; set; }
  public string TempDirectory { get; set; }
}

public static class DownloadSettingsService
{
  // Startup and maintenance operations share this short-lived lock. Downloads
  // themselves run outside it; HasPendingWork also covers cancelled workers
  // until their filesystem cleanup has finished.
  internal static readonly object Gate = new();

  public static DownloadSettingsSnapshot GetStatus() => new() {
    TempDownloadMode = DownloadStorageSettingsStore.GetTempDownloadMode(),
    TempDirectory = DownloadStorageSettingsStore.GetTempDirectory()
  };

  public static DownloadSettingsSnapshot SetTempDownloadMode(bool enabled)
  {
    lock (Gate)
    {
      if (enabled == DownloadStorageSettingsStore.GetTempDownloadMode()) return GetStatus();
      EnsureIdle();
      DownloadStorageSettingsStore.SetTempDownloadMode(enabled);
      ActivityChanges.Notify(ActivityTopic.Settings | ActivityTopic.Library);
      return GetStatus();
    }
  }

  public static DownloadSettingsSnapshot FlushTemp()
  {
    lock (Gate)
    {
      EnsureIdle();
      string root = DownloadStorageSettingsStore.GetDefaultRoot();
      string directory = DownloadStorageSettingsStore.GetTempDirectory();
      if (DownloadStorageMigrationService.IsDirectoryInUse(directory))
        throw new LevelUpdateException("downloaded_level_in_use", "Close the temporary level before clearing it.");
      ManagedDownloadFiles.EnsureSafeTree(root, directory);
      Directory.CreateDirectory(directory);
      foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
      foreach (string child in Directory.GetDirectories(directory)) Directory.Delete(child, true);
      ActivityChanges.Notify(ActivityTopic.Settings);
      return GetStatus();
    }
  }

  public static bool DeleteLevel(string id)
  {
    string normalized = DownloadCachePaths.NormalizeLevelId(id);
    if (!int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number <= 0)
      throw new LevelUpdateException("invalid_level_id", "A positive numeric level ID is required.");
    lock (Gate)
    {
      EnsurePersistentMode();
      EnsureIdle();
      string root = DownloadCachePaths.GetDownloadRoot();
      string directory = Path.Combine(root, DownloadCachePaths.BuildTufCacheKey(number.ToString(CultureInfo.InvariantCulture)));
      if (DownloadStorageMigrationService.IsDirectoryInUse(directory))
        throw new LevelUpdateException("downloaded_level_in_use", "Close the downloaded level before deleting it.");
      ManagedDownloadFiles.EnsureSafeTree(root, directory);
      if (!Directory.Exists(directory)) return false;
      try { Directory.Delete(directory, true); }
      finally { DownloadLibraryService.NotifyLevelDeleted(); }
      return true;
    }
  }

  internal static void EnsurePersistentMode()
  {
    if (DownloadStorageSettingsStore.GetTempDownloadMode())
      throw new LevelUpdateException("temp_download_mode_enabled", "Disable temporary downloads to manage the saved download library.");
  }

  private static void EnsureIdle()
  {
    if (DownloadStorageMigrationService.IsMigrationActive)
      throw new LevelUpdateException("storage_migration_in_progress", "Wait for storage migration to finish.");
    if (LevelUpdateCheckBatchService.IsActive)
      throw new LevelUpdateException("downloaded_level_batch_check_in_progress", "Wait for the batch operation to finish.");
    if (LevelJobService.HasPendingWork())
      throw new LevelUpdateException("download_jobs_active", "Wait for downloads and level selections to finish.");
  }
}
