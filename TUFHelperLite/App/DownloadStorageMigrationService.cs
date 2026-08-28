using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Threading;
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
  private static CancellationTokenSource _changeCancellation;
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

  public static DownloadStorageMigrationSnapshot StartChange(string selectionToken, bool useDefault)
  {
    string target;
    string selectionKind;
    if (useDefault)
    {
      target = DownloadStorageSettingsStore.GetDefaultRoot();
      try { target = ValidateChangeTarget(target, out selectionKind, true); }
      catch (DownloadStorageMigrationException exception) { return Failure(exception.Code, exception.Message); }
    }
    else if (!DownloadFolderPickerCoordinator.TryConsumeSelection(selectionToken, out target, out selectionKind))
    {
      return Failure("selection_token_invalid", "The selected folder token is missing or expired.");
    }

    return StartChangeForTarget(target, useDefault);
  }

  internal static DownloadStorageMigrationSnapshot StartChangeForTarget(string target, bool allowMissing = false)
  {
    string selectionKind;
    try
    {
      target = ValidateChangeTarget(target, out selectionKind, allowMissing);
      EnsureCanStart(target);
    }
    catch (DownloadStorageMigrationException exception)
    {
      return Failure(exception.Code, exception.Message);
    }
    lock (Gate)
    {
      if (IsActiveState(_snapshot.State))
        return Failure("storage_migration_in_progress", "A download storage operation is already running.");
      _changeCancellation?.Dispose();
      _changeCancellation = new CancellationTokenSource();
      _snapshot = new DownloadStorageMigrationSnapshot
      {
        OperationId = Guid.NewGuid().ToString("N"),
        OperationKind = selectionKind == "merge_reconnect" ? "merge_reconnect" : "migration",
        SelectionKind = selectionKind,
        State = "copying",
        Phase = selectionKind == "merge_reconnect" ? "preflight" : "copying",
        SourceDirectory = Path.GetFullPath(DownloadCachePaths.GetDownloadRoot()),
        TargetDirectory = target,
        Message = selectionKind == "merge_reconnect"
          ? "Checking the existing download folder before reconnecting."
          : "Preparing downloaded levels for migration."
      };
      ApplyLocationFields(_snapshot);
      SaveJournalLocked();
      QueueWorkerLocked();
      return Clone(_snapshot);
    }
  }

  public static DownloadStorageMigrationSnapshot CancelChange()
  {
    lock (Gate)
    {
      if (_snapshot.OperationKind != "merge_reconnect" || _snapshot.Phase is "switching" or "cleaning")
        return Failure("storage_change_not_cancellable", "This storage operation can no longer be cancelled.");
      if (!IsActiveState(_snapshot.State)) return Clone(_snapshot);
      _changeCancellation?.Cancel();
      _snapshot.Message = "Cancelling storage reconnection.";
      return Clone(_snapshot);
    }
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
        OperationKind = "migration",
        SelectionKind = "migration",
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
      if (_snapshot.OperationKind == "merge_reconnect" && _snapshot.State == "copying")
      {
        _changeCancellation?.Dispose();
        _changeCancellation = new CancellationTokenSource();
      }
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

  public static string ValidateChangeTarget(string directory, out string selectionKind, bool allowMissing = false)
  {
    selectionKind = "migration";
    string target = ValidateTargetPath(directory, allowMissing);
    bool hasLevels = false;
    foreach (string entry in Directory.EnumerateFileSystemEntries(target))
    {
      if (DownloadStorageIdentityService.IsMarkerOrOsMetadata(entry)) continue;
      if (!Directory.Exists(entry) || !DownloadCachePaths.TryParseTufCacheKey(Path.GetFileName(entry), out int id) ||
          !DownloadLibraryService.IsValidDownloadedLevelDirectory(entry, id))
        throw new DownloadStorageMigrationException("storage_target_unrecognized_content",
          "The selected folder contains files that are not a TUFHelperLite download library.");
      hasLevels = true;
    }
    selectionKind = hasLevels ? "merge_reconnect" : "migration";
    return target;
  }

  private static string ValidateTargetPath(string directory, bool allowMissing)
  {
    if (string.IsNullOrWhiteSpace(directory))
      throw new DownloadStorageMigrationException("storage_target_required", "Choose a TUFHelperLite download folder.");
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
    if (LevelUpdateCheckBatchService.IsActive)
      throw new DownloadStorageMigrationException("downloaded_level_batch_check_in_progress", "Wait for the batch update check to finish.");
    if (LevelJobService.HasActiveJobs())
      throw new DownloadStorageMigrationException("download_jobs_active", "Wait for all downloads and level selections to finish.");

    string source = Normalize(DownloadCachePaths.GetDownloadRoot());
    if (_levelInUseProbe(source))
      throw new DownloadStorageMigrationException("downloaded_level_in_use",
        "Close the downloaded level or return to the main menu before moving the download folder.");
    if (_levelInUseProbe(target))
      throw new DownloadStorageMigrationException("downloaded_level_in_use",
        "Close the level opened from the selected folder before reconnecting it.");

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

      if (work.OperationKind == "merge_reconnect")
      {
        if (PathsEqual(DownloadStorageSettingsStore.GetDownloadRoot(), work.TargetDirectory) &&
            work.Phase is "switching" or "cleaning")
        {
          CleanupSource(work);
          return;
        }
        MergeAndReconnect(work, _changeCancellation?.Token ?? CancellationToken.None);
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
      lock (Gate)
      {
        _workerRunning = false;
        if (!IsActiveState(_snapshot.State))
        {
          _changeCancellation?.Dispose();
          _changeCancellation = null;
        }
      }
    }
  }

  private static void MergeAndReconnect(DownloadStorageMigrationSnapshot work, CancellationToken token)
  {
    string planPath = _journalPath + ".merge-plan";
    string temporaryPlan = planPath + ".tmp";
    string stagingRoot = Path.Combine(work.TargetDirectory, ".tufhelperlite-merge-work-" + work.OperationId);
    try
    {
      UpdatePhase("copying", "preflight", "Checking duplicate levels against the latest official files.");
      using (StreamWriter plan = new(temporaryPlan, false))
      {
        if (Directory.Exists(work.SourceDirectory))
        {
          foreach (string sourceLevel in Directory.EnumerateDirectories(work.SourceDirectory, "tuf-*", SearchOption.TopDirectoryOnly))
          {
            token.ThrowIfCancellationRequested();
            if (!DownloadCachePaths.TryParseTufCacheKey(Path.GetFileName(sourceLevel), out int id) ||
                !DownloadLibraryService.IsValidDownloadedLevelDirectory(sourceLevel, id)) continue;
            string targetLevel = Path.Combine(work.TargetDirectory, DownloadCachePaths.BuildTufCacheKey(id.ToString()));
            string winner = !Directory.Exists(targetLevel)
              ? "source"
              : DownloadLibraryService.ResolveDuplicateWinner(id, sourceLevel, targetLevel, token);
            plan.WriteLine(id + "|" + winner);
          }
        }
      }
      if (File.Exists(planPath)) File.Replace(temporaryPlan, planPath, null);
      else File.Move(temporaryPlan, planPath);

      DownloadStorageIdentityService.Ensure(work.TargetDirectory);
      Directory.CreateDirectory(stagingRoot);
      int filesTotal = CountMergeFiles(planPath, work.SourceDirectory);
      int filesProcessed = 0;
      long bytesProcessed = 0;
      long bytesTotal = CountMergeBytes(planPath, work.SourceDirectory);
      UpdateProgress("copying", 0, filesTotal, 0, bytesTotal, "Copying levels into the existing download folder.");
      UpdatePhase("copying", "copying", "Copying levels into the existing download folder.");
      foreach (string line in File.ReadLines(planPath))
      {
        token.ThrowIfCancellationRequested();
        string[] parts = line.Split('|');
        if (parts.Length != 2 || parts[1] != "source") continue;
        string name = DownloadCachePaths.BuildTufCacheKey(parts[0]);
        string sourceLevel = Path.Combine(work.SourceDirectory, name);
        string stagedLevel = Path.Combine(stagingRoot, name);
        CopyDirectoryVerified(sourceLevel, stagedLevel, token, ref filesProcessed, filesTotal,
          ref bytesProcessed, bytesTotal);
      }

      token.ThrowIfCancellationRequested();
      UpdatePhase("switching", "switching", "Activating the merged download library.");
      foreach (string line in File.ReadLines(planPath))
      {
        string[] parts = line.Split('|');
        if (parts.Length != 2 || parts[1] != "source") continue;
        string name = DownloadCachePaths.BuildTufCacheKey(parts[0]);
        string targetLevel = Path.Combine(work.TargetDirectory, name);
        string stagedLevel = Path.Combine(stagingRoot, name);
        string backup = Path.Combine(work.TargetDirectory, ".tufhelperlite-merge-backup-" + parts[0]);
        lock (Gate)
        {
          _snapshot.CurrentLevelId = parts[0];
          SaveJournalLocked();
        }
        if (Directory.Exists(backup) && Directory.Exists(targetLevel)) Directory.Delete(backup, true);
        if (!Directory.Exists(stagedLevel)) continue;
        if (Directory.Exists(targetLevel)) Directory.Move(targetLevel, backup);
        Directory.Move(stagedLevel, targetLevel);
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
      }

      if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
      DownloadStorageSettingsStore.SetDownloadRoot(work.TargetDirectory);
      DownloadLibraryService.NotifyStorageRootChanged();
      UpdatePhase("cleaning", "cleaning", "Removing the previous download folder.");
      CleanupSource(work);
      TryDeleteFile(planPath);
    }
    catch (OperationCanceledException)
    {
      TryDeleteDirectory(stagingRoot);
      TryDeleteFile(planPath);
      TryDeleteFile(temporaryPlan);
      lock (Gate)
      {
        _snapshot.State = "cancelled";
        _snapshot.ErrorCode = null;
        _snapshot.Message = "Storage reconnection was cancelled before activation.";
        TryDeleteJournal();
      }
    }
  }

  private static int CountMergeFiles(string planPath, string sourceRoot)
  {
    int count = 0;
    foreach (string line in File.ReadLines(planPath))
    {
      string[] parts = line.Split('|');
      if (parts.Length == 2 && parts[1] == "source")
        count += Directory.EnumerateFiles(Path.Combine(sourceRoot, DownloadCachePaths.BuildTufCacheKey(parts[0])), "*", SearchOption.AllDirectories).Count();
    }
    return count;
  }

  private static long CountMergeBytes(string planPath, string sourceRoot)
  {
    long total = 0;
    foreach (string line in File.ReadLines(planPath))
    {
      string[] parts = line.Split('|');
      if (parts.Length != 2 || parts[1] != "source") continue;
      foreach (string file in Directory.EnumerateFiles(Path.Combine(sourceRoot, DownloadCachePaths.BuildTufCacheKey(parts[0])), "*", SearchOption.AllDirectories))
        total = checked(total + new FileInfo(file).Length);
    }
    return total;
  }

  private static void CopyDirectoryVerified(string source, string target, CancellationToken token,
    ref int filesProcessed, int filesTotal, ref long bytesProcessed, long bytesTotal)
  {
    foreach (string sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
      token.ThrowIfCancellationRequested();
      string targetFile = Path.Combine(target, Path.GetRelativePath(source, sourceFile));
      Directory.CreateDirectory(Path.GetDirectoryName(targetFile));
      if (!FilesMatch(sourceFile, targetFile))
      {
        string partial = targetFile + ".tufhelperlite-partial";
        File.Copy(sourceFile, partial, true);
        if (File.Exists(targetFile)) File.Delete(targetFile);
        File.Move(partial, targetFile);
      }
      if (!FilesMatch(sourceFile, targetFile))
        throw new DownloadStorageMigrationException("storage_verification_failed", "A merged file failed verification.");
      filesProcessed++;
      bytesProcessed += new FileInfo(sourceFile).Length;
      UpdateProgress("copying", filesProcessed, filesTotal, bytesProcessed, bytesTotal, "Copying levels into the existing download folder.");
    }
  }

  private static void UpdatePhase(string state, string phase, string message)
  {
    lock (Gate)
    {
      _snapshot.State = state;
      _snapshot.Phase = phase;
      _snapshot.Message = message;
      SaveJournalLocked();
    }
  }

  private static void TryDeleteDirectory(string path)
  {
    try { if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Directory.Delete(path, true); } catch { }
  }

  private static void TryDeleteFile(string path)
  {
    try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
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
    if (leftHash.Length != rightHash.Length) return false;
    for (int index = 0; index < leftHash.Length; index++)
    {
      if (leftHash[index] != rightHash[index]) return false;
    }
    return true;
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
    OperationKind = value.OperationKind,
    SelectionKind = value.SelectionKind,
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
    DefaultDirectory = value.DefaultDirectory,
    CurrentLevelId = value.CurrentLevelId,
    Phase = value.Phase
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
