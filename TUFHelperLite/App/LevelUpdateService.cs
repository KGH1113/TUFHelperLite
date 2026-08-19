using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using TUFHelperLite.Domain.Downloads;
using TUFHelperLite.Domain.Errors;
using TUFHelperLite.Domain.Jobs;
using TUFHelperLite.Infrastructure.Downloads;
using TUFHelperLite.Infrastructure.Tuforums;

namespace TUFHelperLite.App;

public static class LevelUpdateService
{
  private const int JournalVersion = 1;
  private static readonly object Gate = new();
  private static string _journalPath;
  private static Func<string, TufLevelInfo> _levelProvider = TuforumsClient.GetLevelMetadataById;
  private static Func<string, string, CancellationToken, Action<TUFHelperLite.Domain.Levels.LevelDownloadProgress>, TUFHelperLite.Domain.Levels.LevelDownloadResult> _stagingDownloader =
    LevelArchiveDownloader.DownloadToDirectory;

  public static void Initialize(string installPath)
  {
    _journalPath = Path.Combine(installPath ?? AppDomain.CurrentDomain.BaseDirectory, "DownloadUpdateJournal.json");
    Recover();
  }

  public static void Check(int id, DownloadJob job)
  {
    EnsureAvailable();
    DownloadedLevelUpdateDescriptor current = DownloadLibraryService.GetUpdateDescriptor(id);
    TufLevelInfo remote = GetRemote(id);
    job.SetLevelInfo(remote.Song, remote.Artist, FirstNonEmpty(remote.Creator, remote.Charter, remote.Team));
    job.SetDifficultyId(remote.DiffId);

    bool hasComparableRevision = !string.IsNullOrWhiteSpace(remote.FileId) &&
      !string.IsNullOrWhiteSpace(current.DownloadedFileId);
    if (hasComparableRevision)
    {
      bool upToDate = string.Equals(remote.FileId, current.DownloadedFileId, StringComparison.Ordinal);
      DownloadedLevelItem item = DownloadLibraryService.RecordUpdateCheck(id, remote, null, upToDate);
      DownloadedLevelUpdateDescriptor saved = DownloadLibraryService.GetUpdateDescriptor(id);
      job.CompleteUpdate(item, upToDate ? "up_to_date" : "update_available",
        saved.DownloadedFileId, saved.AvailableFileId, saved.AvailableUpdatedAtUtc);
      return;
    }

    string staging = CreateStagingPath("check", id, job.JobId);
    try
    {
      job.Report("comparing", "Downloading latest level to compare", -1);
      _stagingDownloader(remote.DownloadLink, staging, job.Token, job.Report);
      string candidateHash = DownloadLibraryService.CalculatePayloadHash(staging);
      string installedHash = current.InstalledPayloadHash;
      if (string.IsNullOrWhiteSpace(installedHash))
        installedHash = DownloadLibraryService.CalculatePayloadHash(current.Directory);
      bool upToDate = string.Equals(candidateHash, installedHash, StringComparison.OrdinalIgnoreCase);
      DownloadedLevelItem item = DownloadLibraryService.RecordUpdateCheck(id, remote, candidateHash, upToDate);
      DownloadedLevelUpdateDescriptor saved = DownloadLibraryService.GetUpdateDescriptor(id);
      job.CompleteUpdate(item, upToDate ? "up_to_date" : "update_available",
        saved.DownloadedFileId, saved.AvailableFileId, saved.AvailableUpdatedAtUtc);
    }
    finally
    {
      TryDeleteDirectory(staging);
    }
  }

  public static void Update(int id, DownloadJob job)
  {
    EnsureAvailable();
    DownloadedLevelUpdateDescriptor current = DownloadLibraryService.GetUpdateDescriptor(id);
    if (DownloadStorageMigrationService.IsDirectoryInUse(current.Directory))
      throw new LevelUpdateException("downloaded_level_in_use", "Close the downloaded level before updating it.");

    TufLevelInfo remote = GetRemote(id);
    job.SetLevelInfo(remote.Song, remote.Artist, FirstNonEmpty(remote.Creator, remote.Charter, remote.Team));
    job.SetDifficultyId(remote.DiffId);
    string staging = CreateStagingPath("update", id, job.JobId);
    string backup = current.Directory + ".update-backup-" + job.JobId;
    LevelUpdateJournal journal = new()
    {
      Version = JournalVersion,
      Id = id,
      TargetDirectory = current.Directory,
      StagingDirectory = staging,
      BackupDirectory = backup,
      Phase = "downloading",
      Previous = current,
      Remote = remote
    };

    try
    {
      _stagingDownloader(remote.DownloadLink, staging, job.Token, job.Report);
      journal.InstalledPayloadHash = DownloadLibraryService.CalculatePayloadHash(staging);
      journal.SizeBytes = DownloadLibraryService.GetPayloadSize(staging);
      if (string.Equals(journal.InstalledPayloadHash, current.InstalledPayloadHash, StringComparison.OrdinalIgnoreCase))
      {
        DownloadedLevelItem unchanged = DownloadLibraryService.RecordUpdateCheck(id, remote, null, true);
        DownloadedLevelUpdateDescriptor saved = DownloadLibraryService.GetUpdateDescriptor(id);
        job.CompleteUpdate(unchanged, "up_to_date", saved.DownloadedFileId, null, null);
        TryDeleteDirectory(staging);
        return;
      }

      journal.Phase = "staged";
      SaveJournal(journal);
      job.Report("switching", "Activating updated level", 1);
      if (Directory.Exists(backup)) TryDeleteDirectory(backup);
      Directory.Move(current.Directory, backup);
      journal.Phase = "backup_moved";
      SaveJournal(journal);
      Directory.Move(staging, current.Directory);
      journal.Phase = "activated";
      SaveJournal(journal);

      DownloadedLevelItem item = DownloadLibraryService.RecordActivatedUpdate(
        id, current.Directory, current, remote, journal.InstalledPayloadHash, journal.SizeBytes);
      journal.Phase = "cleanup_pending";
      SaveJournal(journal);
      if (TryDeleteDirectory(backup)) ClearJournal();
      job.CompleteUpdate(item, "up_to_date", remote.FileId, null, null);
    }
    catch
    {
      if (journal.Phase is "downloading" or "staged")
      {
        TryDeleteDirectory(staging);
        ClearJournal();
      }
      else
      {
        Recover();
      }
      throw;
    }
  }

  private static TufLevelInfo GetRemote(int id)
  {
    TufLevelInfo remote = _levelProvider(id.ToString(CultureInfo.InvariantCulture));
    if (remote.IsDeleted)
      throw new LevelUpdateException("downloaded_level_deleted", "This level is no longer available on TUF.");
    if (string.IsNullOrWhiteSpace(remote.DownloadLink))
      throw new LevelUpdateException("downloaded_level_unavailable", "This level does not have a downloadable file.");
    return remote;
  }

  private static void EnsureAvailable()
  {
    if (DownloadStorageMigrationService.IsMigrationActive)
      throw new LevelUpdateException("storage_migration_in_progress", "Wait for storage migration to finish.");
  }

  internal static void SetDependenciesForTests(
    Func<string, TufLevelInfo> levelProvider,
    Func<string, string, CancellationToken, Action<TUFHelperLite.Domain.Levels.LevelDownloadProgress>, TUFHelperLite.Domain.Levels.LevelDownloadResult> stagingDownloader)
  {
    _levelProvider = levelProvider ?? TuforumsClient.GetLevelMetadataById;
    _stagingDownloader = stagingDownloader ?? LevelArchiveDownloader.DownloadToDirectory;
  }

  private static string CreateStagingPath(string kind, int id, string jobId)
  {
    string root = Path.Combine(DownloadCachePaths.GetDownloadRoot(), ".tufhelperlite-update-work");
    Directory.CreateDirectory(root);
    return Path.Combine(root, kind + "-" + id + "-" + jobId);
  }

  private static void Recover()
  {
    lock (Gate)
    {
      LevelUpdateJournal journal = LoadJournal();
      if (journal == null) return;
      try
      {
        if (journal.Phase == "staged")
        {
          if (!Directory.Exists(journal.TargetDirectory) && Directory.Exists(journal.BackupDirectory))
            Directory.Move(journal.BackupDirectory, journal.TargetDirectory);
          TryDeleteDirectory(journal.StagingDirectory);
          ClearJournalLocked();
          return;
        }
        if (journal.Phase == "backup_moved")
        {
          if (Directory.Exists(journal.BackupDirectory))
          {
            if (!TryDeleteDirectory(journal.TargetDirectory)) return;
            Directory.Move(journal.BackupDirectory, journal.TargetDirectory);
          }
          TryDeleteDirectory(journal.StagingDirectory);
          ClearJournalLocked();
          return;
        }
        if (journal.Phase is "activated" or "cleanup_pending")
        {
          if (Directory.Exists(journal.TargetDirectory))
            DownloadLibraryService.RecordActivatedUpdate(journal.Id, journal.TargetDirectory,
              journal.Previous, journal.Remote, journal.InstalledPayloadHash, journal.SizeBytes);
          if (!TryDeleteDirectory(journal.BackupDirectory)) return;
          TryDeleteDirectory(journal.StagingDirectory);
          ClearJournalLocked();
          return;
        }
        TryDeleteDirectory(journal.StagingDirectory);
        ClearJournalLocked();
      }
      catch (Exception exception)
      {
        Main.Instance?.Warning("Failed to recover level update: " + exception.Message);
      }
    }
  }

  private static void SaveJournal(LevelUpdateJournal journal)
  {
    lock (Gate)
    {
      string temporary = _journalPath + ".tmp";
      File.WriteAllText(temporary, JsonConvert.SerializeObject(journal, Formatting.Indented));
      if (File.Exists(_journalPath)) File.Replace(temporary, _journalPath, null);
      else File.Move(temporary, _journalPath);
    }
  }

  private static LevelUpdateJournal LoadJournal()
  {
    try
    {
      if (string.IsNullOrWhiteSpace(_journalPath) || !File.Exists(_journalPath)) return null;
      LevelUpdateJournal journal = JsonConvert.DeserializeObject<LevelUpdateJournal>(File.ReadAllText(_journalPath));
      return journal?.Version == JournalVersion ? journal : null;
    }
    catch { return null; }
  }

  private static void ClearJournal()
  {
    lock (Gate) ClearJournalLocked();
  }

  private static void ClearJournalLocked()
  {
    if (File.Exists(_journalPath)) File.Delete(_journalPath);
    if (File.Exists(_journalPath + ".tmp")) File.Delete(_journalPath + ".tmp");
  }

  private static bool TryDeleteDirectory(string path)
  {
    if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return true;
    try
    {
      Directory.Delete(path, true);
      return true;
    }
    catch { return false; }
  }

  private static string FirstNonEmpty(params string[] values)
  {
    foreach (string value in values)
      if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
    return null;
  }

  private sealed class LevelUpdateJournal
  {
    public int Version { get; set; }
    public int Id { get; set; }
    public string TargetDirectory { get; set; }
    public string StagingDirectory { get; set; }
    public string BackupDirectory { get; set; }
    public string Phase { get; set; }
    public DownloadedLevelUpdateDescriptor Previous { get; set; }
    public TufLevelInfo Remote { get; set; }
    public string InstalledPayloadHash { get; set; }
    public long SizeBytes { get; set; }
  }
}
