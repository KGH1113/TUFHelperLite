using System;
using System.IO;
using System.Linq;
using System.Threading;
using TUFHelperLite.App;
using TUFHelperLite.Domain.Errors;
using TUFHelperLite.Domain.Levels;
using TUFHelperLite.Infrastructure.Settings;

namespace TUFHelperLite.Infrastructure.Downloads;

internal static class TempLevelDownloader
{
  internal static TUFHelperLite.App.Ports.ITempLevelRelease LevelRelease { get; set; }
  private static readonly object Gate = new();
  private static Func<string, string, CancellationToken, Action<LevelDownloadProgress>, LevelDownloadResult> _download =
    LevelArchiveDownloader.DownloadToDirectory;

  internal static LevelDownloadResult Download(string url, CancellationToken token, Action<LevelDownloadProgress> progress)
  {
    lock (Gate)
    {
      token.ThrowIfCancellationRequested();
      if (LevelJobService.HasPendingTempSelection())
        throw new LevelUpdateException("download_jobs_active", "Select or cancel the previous temporary level before downloading another one.");
      string root = DownloadStorageSettingsStore.GetDefaultRoot();
      string destination = DownloadStorageSettingsStore.GetTempDirectory();
      string workRoot = Path.Combine(root, ".tufhelperlite-temp-work");
      ManagedDownloadFiles.EnsureSafeTree(root, destination);
      ManagedDownloadFiles.EnsureSafeTree(root, workRoot);
      Directory.CreateDirectory(workRoot);
      string staging = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
      string backup = staging + ".previous";
      bool activated = false;
      try
      {
        LevelDownloadResult result = _download(url, staging, token, progress);
        token.ThrowIfCancellationRequested();
        if (result.LevelPaths.Count == 0)
          throw new FileNotFoundException("No .adofai file was found in the downloaded archive.", staging);
        ManagedDownloadFiles.EnsureSafeTree(root, staging);
        ManagedDownloadFiles.EnsureSafeTree(root, destination);
        if (DownloadStorageMigrationService.IsDirectoryInUse(destination))
        {
          if (PendingLevelOpen.IsInside(destination))
            throw new LevelUpdateException("download_jobs_active", "Wait for the previous temporary level to open before replacing it.");
          var release = LevelRelease ?? throw new LevelUpdateException("downloaded_level_in_use", "Close the temporary level before replacing it.");
          progress?.Invoke(new LevelDownloadProgress { Stage = "waiting_for_level_close", Progress = -1,
            Message = "Waiting for the current temporary level to close. Confirm any unsaved changes in ADOFAI, or cancel this download." });
          release.ReleaseAsync(destination, token).GetAwaiter().GetResult();
          token.ThrowIfCancellationRequested();
          if (DownloadStorageMigrationService.IsDirectoryInUse(destination))
            throw new LevelUpdateException("downloaded_level_in_use", "The previous temporary level is still in use.");
          ManagedDownloadFiles.EnsureSafeTree(root, staging);
          ManagedDownloadFiles.EnsureSafeTree(root, destination);
        }
        if (Directory.Exists(destination)) Directory.Move(destination, backup);
        try { Directory.Move(staging, destination); }
        catch
        {
          if (Directory.Exists(backup)) Directory.Move(backup, destination);
          throw;
        }
        activated = true;
        result.LevelPaths = result.LevelPaths.Select(path => Path.Combine(destination, Path.GetRelativePath(staging, path))).ToList();
        result.SelectedLevelPath = result.SelectedLevelPath == null ? null : Path.Combine(destination, Path.GetRelativePath(staging, result.SelectedLevelPath));
        result.Directory = destination;
        result.FromCache = false;
        return result;
      }
      finally
      {
        TryClean(staging);
        if (activated) TryClean(backup);
        try { if (!Directory.EnumerateFileSystemEntries(workRoot).Any()) Directory.Delete(workRoot); } catch { }
      }
    }
  }

  internal static void SetDownloaderForTests(Func<string, string, CancellationToken, Action<LevelDownloadProgress>, LevelDownloadResult> downloader) =>
    _download = downloader ?? LevelArchiveDownloader.DownloadToDirectory;

  private static void TryClean(string path)
  {
    try { if (Directory.Exists(path)) Directory.Delete(path, true); }
    catch (Exception error) { Main.Instance?.Warning("Failed to remove temporary download work files: " + error.Message); }
  }
}
