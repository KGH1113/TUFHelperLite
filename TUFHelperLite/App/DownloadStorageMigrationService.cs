using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TUFHelperLite.Domain.Storage;
using TUFHelperLite.Infrastructure.Downloads;
using TUFHelperLite.Infrastructure.Settings;

namespace TUFHelperLite.App;

public sealed class DownloadStorageMigrationException : Exception
{
  public DownloadStorageMigrationException(string code, string message) : base(message) => Code = code;
  public string Code { get; }
}

public static class DownloadStorageMigrationService
{
  private static readonly object Gate = new();
  private static string _journalPath;
  private static DownloadStorageMigrationSnapshot _snapshot = new();
  private static bool _workerRunning;
  private static Func<string, bool> _levelInUseProbe = IsDownloadedLevelInUse;

  public static bool IsMigrationActive
  {
    get
    {
      lock (Gate) return IsActiveState(_snapshot.State);
    }
  }

  public static void Initialize(string installPath)
  {
    _journalPath = Path.Combine(installPath ?? AppDomain.CurrentDomain.BaseDirectory, "DownloadMigration.json");
    lock (Gate)
    {
      _snapshot = LoadJournal() ?? IdleSnapshot();
      ApplyLocationFields(_snapshot);
      if (IsActiveState(_snapshot.State) || _snapshot.State == "cleanup_pending")
        QueueWorkerLocked();
    }
  }

  public static DownloadStorageMigrationSnapshot GetStatus()
  {
    lock (Gate)
    {
      DownloadStorageMigrationSnapshot copy = Clone(_snapshot);
      ApplyLocationFields(copy);
      return copy;
    }
  }

  public static DownloadStorageMigrationSnapshot Start(string selectionToken, bool useDefault)
  {
    string target;
    if (useDefault)
      target = DownloadStorageSettingsStore.GetDefaultRoot();
    else if (!DownloadFolderPickerCoordinator.TryConsumeSelection(selectionToken, out target))
      return Failure("selection_token_invalid", "The selected folder token is missing or expired.");

    return StartForTarget(target, useDefault);
  }

  internal static DownloadStorageMigrationSnapshot StartForTarget(string target, bool allowMissing = false)
  {
    try
    {
      target = ValidateSelectedTarget(target, allowMissing);
      EnsureCanStart(target);
    }
    catch (DownloadStorageMigrationException exception)
    {
      return Failure(exception.Code, exception.Message);
    }

    lock (Gate)
    {
      if (IsActiveState(_snapshot.State))
        return Failure("storage_migration_in_progress", "A download storage migration is already running.");
      if (_snapshot.State == "failed" && !string.IsNullOrWhiteSpace(_snapshot.TargetDirectory))
        return Failure("storage_migration_retry_required", "Retry the failed migration before choosing another folder.");

      _snapshot = new DownloadStorageMigrationSnapshot
      {
        OperationId = Guid.NewGuid().ToString("N"),
        State = "copying",
        SourceDirectory = Path.GetFullPath(DownloadCachePaths.GetDownloadRoot()),
        TargetDirectory = target,
        Message = "Preparing downloaded levels for migration."
      };
      ApplyLocationFields(_snapshot);
      SaveJournalLocked();
      QueueWorkerLocked();
      return Clone(_snapshot);
    }
  }

  public static DownloadStorageMigrationSnapshot Retry()
  {
    lock (Gate)
    {
      if (_snapshot.State != "failed" && _snapshot.State != "cleanup_pending")
        return Failure("storage_migration_not_retryable", "There is no failed migration to retry.");
      _snapshot.ErrorCode = null;
      _snapshot.Message = "Retrying download storage migration.";
      _snapshot.State = _snapshot.State == "cleanup_pending" ? "cleaning" : "copying";
      SaveJournalLocked();
      QueueWorkerLocked();
      return Clone(_snapshot);
    }
  }

  internal static bool WaitForWorkerForTests(int timeoutMilliseconds = 5000)
  {
    DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
    while (DateTime.UtcNow < deadline)
    {
      lock (Gate)
      {
        if (!_workerRunning) return true;
      }
      System.Threading.Thread.Sleep(10);
    }
    return false;
  }

  internal static void SetLevelInUseProbeForTests(Func<string, bool> probe)
  {
    _levelInUseProbe = probe ?? IsDownloadedLevelInUse;
  }

  public static bool IsDirectoryInUse(string directory)
  {
    return !string.IsNullOrWhiteSpace(directory) && _levelInUseProbe(Normalize(directory));
  }

  public static string ValidateSelectedTarget(string directory, bool allowMissing = false)
  {
    if (string.IsNullOrWhiteSpace(directory))
      throw new DownloadStorageMigrationException("storage_target_required", "Choose an empty download folder.");

    string target;
    try { target = Normalize(directory); }
    catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
    {
      throw new DownloadStorageMigrationException("storage_target_invalid", "The selected folder path is invalid.");
    }

    string source = Normalize(DownloadCachePaths.GetDownloadRoot());
    string root = Normalize(Path.GetPathRoot(target));
    if (PathsEqual(target, root))
      throw new DownloadStorageMigrationException("storage_target_is_root", "A drive root cannot be used as the download folder.");
    if (PathsEqual(source, target))
      throw new DownloadStorageMigrationException("storage_target_unchanged", "The selected folder is already in use.");
    if (IsInside(target, source) || IsInside(source, target))
      throw new DownloadStorageMigrationException("storage_target_overlaps_source", "The new folder cannot contain or be inside the current folder.");

    if (!Directory.Exists(target))
    {
      if (!allowMissing)
        throw new DownloadStorageMigrationException("storage_target_missing", "The selected folder no longer exists.");
      Directory.CreateDirectory(target);
    }

    if (Directory.EnumerateFileSystemEntries(target).Any())
      throw new DownloadStorageMigrationException("storage_target_not_empty", "The selected folder must be empty.");

    try
    {
      string probe = Path.Combine(target, ".tufhelperlite-write-test-" + Guid.NewGuid().ToString("N"));
      File.WriteAllText(probe, "test");
      File.Delete(probe);
    }
    catch
    {
      throw new DownloadStorageMigrationException("storage_target_not_writable", "The selected folder is not writable.");
    }
    return target;
  }

  private static void EnsureCanStart(string target)
  {
    if (LevelJobService.HasActiveJobs())
      throw new DownloadStorageMigrationException("download_jobs_active", "Wait for all downloads and level selections to finish.");

    string source = Normalize(DownloadCachePaths.GetDownloadRoot());
    if (_levelInUseProbe(source))
      throw new DownloadStorageMigrationException("downloaded_level_in_use",
        "Close the downloaded level or return to the main menu before moving the download folder.");

    long bytes = Directory.Exists(source)
      ? Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
      : 0;
    try
    {
      DriveInfo drive = new(Path.GetPathRoot(target));
      if (!DiskSpacePolicy.HasSufficientSpace(drive.AvailableFreeSpace, drive.TotalSize, bytes))
        throw new DownloadStorageMigrationException("insufficient_disk_space",
          "The selected folder does not have enough free space for the migration.");
    }
    catch (DownloadStorageMigrationException) { throw; }
    catch { }
  }

  private static void QueueWorkerLocked()
  {
    if (_workerRunning) return;
    _workerRunning = true;
    Task.Run(RunWorker);
  }

  private static void RunWorker()
  {
    try
    {
      DownloadStorageMigrationSnapshot work;
      lock (Gate) work = Clone(_snapshot);
      if (work.State == "cleaning" || work.State == "cleanup_pending")
      {
        CleanupSource(work);
        return;
      }

      CopyAndVerify(work);
      CutOverAndCleanup(work);
    }
    catch (Exception exception)
    {
      lock (Gate)
      {
        _snapshot.State = "failed";
        _snapshot.ErrorCode = exception is DownloadStorageMigrationException migration
          ? migration.Code
          : "storage_migration_failed";
        _snapshot.Message = exception.Message;
        SaveJournalLocked();
      }
      Main.Instance?.LogException(exception);
    }
    finally
    {
      lock (Gate) _workerRunning = false;
    }
  }

  private static void CopyAndVerify(DownloadStorageMigrationSnapshot work)
  {
    string source = work.SourceDirectory;
    string target = work.TargetDirectory;
    Directory.CreateDirectory(target);
    List<string> files = Directory.Exists(source)
      ? Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).OrderBy(path => path).ToList()
      : new List<string>();
    long totalBytes = files.Sum(path => new FileInfo(path).Length);
    UpdateProgress("copying", 0, files.Count, 0, totalBytes, "Copying downloaded levels.");

    int processedFiles = 0;
    long processedBytes = 0;
    foreach (string sourceFile in files)
    {
      string relative = Path.GetRelativePath(source, sourceFile);
      string targetFile = Path.Combine(target, relative);
      Directory.CreateDirectory(Path.GetDirectoryName(targetFile));
      long length = new FileInfo(sourceFile).Length;
      if (!FilesMatch(sourceFile, targetFile))
      {
        string partial = targetFile + ".tufhelperlite-partial";
        File.Copy(sourceFile, partial, true);
        if (File.Exists(targetFile)) File.Delete(targetFile);
        File.Move(partial, targetFile);
      }
      processedFiles++;
      processedBytes += length;
      UpdateProgress("copying", processedFiles, files.Count, processedBytes, totalBytes, "Copying downloaded levels.");
    }

    UpdateProgress("verifying", 0, files.Count, 0, totalBytes, "Verifying copied files.");
    processedFiles = 0;
    processedBytes = 0;
    foreach (string sourceFile in files)
    {
      string targetFile = Path.Combine(target, Path.GetRelativePath(source, sourceFile));
      if (!FilesMatch(sourceFile, targetFile))
        throw new DownloadStorageMigrationException("storage_verification_failed", "A copied file failed verification.");
      processedFiles++;
      processedBytes += new FileInfo(sourceFile).Length;
      UpdateProgress("verifying", processedFiles, files.Count, processedBytes, totalBytes, "Verifying copied files.");
    }
  }

  private static void CutOverAndCleanup(DownloadStorageMigrationSnapshot work)
  {
    UpdateState("switching", "Activating the new download folder.");
    DownloadStorageSettingsStore.SetDownloadRoot(work.TargetDirectory);
    DownloadLibraryService.NotifyStorageRootChanged();
    UpdateState("cleaning", "Removing the previous download folder.");
    CleanupSource(work);
  }

  private static void CleanupSource(DownloadStorageMigrationSnapshot work)
  {
    try
    {
      if (Directory.Exists(work.SourceDirectory)) Directory.Delete(work.SourceDirectory, true);
      lock (Gate)
      {
        _snapshot.State = "completed";
        _snapshot.ErrorCode = null;
        _snapshot.Message = "Download storage migration completed.";
        ApplyLocationFields(_snapshot);
        TryDeleteJournal();
      }
    }
    catch (Exception exception)
    {
      lock (Gate)
      {
        _snapshot.State = "cleanup_pending";
        _snapshot.ErrorCode = "storage_cleanup_pending";
        _snapshot.Message = "The new folder is active, but the previous folder could not be removed: " + exception.Message;
        ApplyLocationFields(_snapshot);
        SaveJournalLocked();
      }
    }
  }

  private static bool FilesMatch(string left, string right)
  {
    if (!File.Exists(right)) return false;
    if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
    using SHA256 sha = SHA256.Create();
    using FileStream leftStream = File.OpenRead(left);
    byte[] leftHash = sha.ComputeHash(leftStream);
    using FileStream rightStream = File.OpenRead(right);
    byte[] rightHash = sha.ComputeHash(rightStream);
    return leftHash.SequenceEqual(rightHash);
  }

  private static void UpdateProgress(string state, int files, int filesTotal, long bytes, long bytesTotal, string message)
  {
    lock (Gate)
    {
      _snapshot.State = state;
      _snapshot.FilesProcessed = files;
      _snapshot.FilesTotal = filesTotal;
      _snapshot.BytesProcessed = bytes;
      _snapshot.BytesTotal = bytesTotal;
      _snapshot.Message = message;
      SaveJournalLocked();
    }
  }

  private static void UpdateState(string state, string message)
  {
    lock (Gate)
    {
      _snapshot.State = state;
      _snapshot.Message = message;
      SaveJournalLocked();
    }
  }

  private static DownloadStorageMigrationSnapshot LoadJournal()
  {
    try
    {
      if (string.IsNullOrWhiteSpace(_journalPath)) return null;
      string readablePath = File.Exists(_journalPath)
        ? _journalPath
        : File.Exists(_journalPath + ".bak")
          ? _journalPath + ".bak"
          : null;
      return readablePath == null
        ? null
        : JsonConvert.DeserializeObject<DownloadStorageMigrationSnapshot>(File.ReadAllText(readablePath));
    }
    catch (Exception exception)
    {
      Main.Instance?.Warning("Failed to read DownloadMigration.json: " + exception.Message);
      return null;
    }
  }

  private static void SaveJournalLocked()
  {
    if (string.IsNullOrWhiteSpace(_journalPath)) return;
    string temporary = _journalPath + ".tmp";
    File.WriteAllText(temporary, JsonConvert.SerializeObject(_snapshot, Formatting.Indented));
    if (!File.Exists(_journalPath))
    {
      File.Move(temporary, _journalPath);
      return;
    }

    string backup = _journalPath + ".bak";
    try
    {
      File.Replace(temporary, _journalPath, backup, true);
      if (File.Exists(backup)) File.Delete(backup);
    }
    catch
    {
      if (File.Exists(temporary)) File.Delete(temporary);
      throw;
    }
  }

  private static void TryDeleteJournal()
  {
    try
    {
      if (File.Exists(_journalPath)) File.Delete(_journalPath);
      if (File.Exists(_journalPath + ".bak")) File.Delete(_journalPath + ".bak");
      if (File.Exists(_journalPath + ".tmp")) File.Delete(_journalPath + ".tmp");
    }
    catch { }
  }

  private static DownloadStorageMigrationSnapshot IdleSnapshot() => new()
  {
    State = "idle",
    Message = "Download storage is ready."
  };

  private static DownloadStorageMigrationSnapshot Failure(string code, string message)
  {
    DownloadStorageMigrationSnapshot snapshot = GetStatus();
    snapshot.State = "failed";
    snapshot.ErrorCode = code;
    snapshot.Message = message;
    return snapshot;
  }

  private static void ApplyLocationFields(DownloadStorageMigrationSnapshot snapshot)
  {
    snapshot.DefaultDirectory = DownloadStorageSettingsStore.GetDefaultRoot();
    snapshot.CurrentDirectory = DownloadStorageSettingsStore.GetDownloadRoot();
    snapshot.IsDefault = PathsEqual(snapshot.CurrentDirectory, snapshot.DefaultDirectory);
    if (string.IsNullOrWhiteSpace(snapshot.SourceDirectory))
      snapshot.SourceDirectory = DownloadStorageSettingsStore.GetDownloadRoot();
  }

  private static DownloadStorageMigrationSnapshot Clone(DownloadStorageMigrationSnapshot value) => new()
  {
    OperationId = value.OperationId,
    State = value.State,
    SourceDirectory = value.SourceDirectory,
    TargetDirectory = value.TargetDirectory,
    CurrentDirectory = value.CurrentDirectory,
    FilesProcessed = value.FilesProcessed,
    FilesTotal = value.FilesTotal,
    BytesProcessed = value.BytesProcessed,
    BytesTotal = value.BytesTotal,
    ErrorCode = value.ErrorCode,
    Message = value.Message,
    IsDefault = value.IsDefault,
    DefaultDirectory = value.DefaultDirectory
  };

  private static string Normalize(string path)
  {
    string fullPath = Path.GetFullPath(path);
    string root = Path.GetPathRoot(fullPath);
    string trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return string.IsNullOrEmpty(trimmed) ? root : trimmed;
  }

  private static bool PathsEqual(string left, string right) =>
    string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

  private static bool IsInside(string path, string parent) =>
    path.StartsWith(Normalize(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

  private static bool IsActiveState(string state) =>
    state is "copying" or "verifying" or "switching" or "cleaning";

  private static bool IsDownloadedLevelInUse(string source)
  {
    try
    {
      string levelPath = ADOBase.levelPath;
      return !string.IsNullOrWhiteSpace(levelPath) && IsInside(Normalize(levelPath), source);
    }
    catch
    {
      return false;
    }
  }
}
