using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using TUFHelperLite.Domain.Errors;
using TUFHelperLite.Domain.Downloads;
using TUFHelperLite.Domain.Jobs;
using TUFHelperLite.Domain.Levels;
using TUFHelperLite.Domain.Storage;
using TUFHelperLite.Infrastructure.Downloads;
using TUFHelperLite.Infrastructure.Settings;
using TUFHelperLite.Integration;
using TUFHelperLite.App;
using TUFHelperLite;

internal static class Program
{
  private static readonly List<string> Failures = new();

  private static int Main()
  {
    string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads");
    if (Directory.Exists(root)) Directory.Delete(root, true);
    try
    {
      string valid = CreateLevel(root, "tuf-12345", "nested/chart.adofai");
      Check("valid nested TUF cache path", 12345, LevelContextResolver.ResolveTufLevelId(valid));
      Check("case-insensitive extension", 42, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-42", "chart.ADOFAI")));
      Check("null", null, LevelContextResolver.ResolveTufLevelId(null));
      Check("blank", null, LevelContextResolver.ResolveTufLevelId("  "));
      Check("non-adofai", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-12345", "notes.txt")));
      Check("missing file", null, LevelContextResolver.ResolveTufLevelId(
        Path.Combine(root, "tuf-12345", "missing.adofai")));
      Check("outside root", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(AppDomain.CurrentDomain.BaseDirectory, "outside", "chart.adofai")));
      Check("root prefix confusion", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root + "-other", "tuf-12345", "chart.adofai")));
      Check("URL cache", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "url-abcdef", "chart.adofai")));
      Check("malformed missing ID", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-", "chart.adofai")));
      Check("malformed nonnumeric ID", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-12x", "chart.adofai")));
      Check("overflow ID", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-999999999999", "chart.adofai")));
      Check("nonpositive ID", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-0", "chart.adofai")));
      Check("file directly under root", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "", "chart.adofai")));
      RunDiskSpacePolicyTests();
      RunCancellationTests();
      RunDownloadStorageMigrationTests();
      RunDownloadLibraryTests();
      RunLevelUpdateTests();
      RunAdofaiIpcMigrationGuardTests();
    }
    finally
    {
      if (Directory.Exists(root)) Directory.Delete(root, true);
      string outside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "outside");
      if (Directory.Exists(outside)) Directory.Delete(outside, true);
      string sibling = root + "-other";
      if (Directory.Exists(sibling)) Directory.Delete(sibling, true);
    }

    if (Failures.Count == 0)
    {
      Console.WriteLine("All TUFHelperLite core tests passed.");
      return 0;
    }
    foreach (string failure in Failures) Console.Error.WriteLine(failure);
    return 1;
  }

  private static string CreateLevel(string root, string directory, string relativePath)
  {
    string path = Path.Combine(root, directory, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "{}");
    return Path.GetFullPath(path);
  }

  private static void RunDiskSpacePolicyTests()
  {
    const long gibibyte = 1024L * 1024 * 1024;
    const long mebibyte = 1024L * 1024;
    CheckLong("minimum disk reserve", gibibyte, DiskSpacePolicy.CalculateReserveBytes(10 * gibibyte));
    CheckLong("percentage disk reserve", 2 * gibibyte, DiskSpacePolicy.CalculateReserveBytes(40 * gibibyte));
    CheckLong("maximum disk reserve", 5 * gibibyte, DiskSpacePolicy.CalculateReserveBytes(200 * gibibyte));
    long requiredBytes = 512 * mebibyte;
    long exactAvailableBytes = gibibyte + requiredBytes;
    CheckTrue("exact disk-space boundary", DiskSpacePolicy.HasSufficientSpace(
      exactAvailableBytes, 10 * gibibyte, requiredBytes));
    CheckFalse("one byte below disk-space boundary", DiskSpacePolicy.HasSufficientSpace(
      exactAvailableBytes - 1, 10 * gibibyte, requiredBytes));
    CheckLong("unknown download length fallback", 0, DiskSpacePolicy.CalculateRemainingBytes(-1, 64 * mebibyte));
    CheckLong("known download remaining bytes", 60, DiskSpacePolicy.CalculateRemainingBytes(100, 40));
    CheckLong("completed download remaining bytes", 0, DiskSpacePolicy.CalculateRemainingBytes(100, 100));

    DownloadJob job = new("tuf", "12345", "https://example.com/level.zip", "tuf-12345", false);
    job.Fail(new InsufficientDiskSpaceException("Not enough storage space.", 512, 2048));
    DownloadJobSnapshot snapshot = job.Snapshot();
    CheckString("disk failure error code", "insufficient_disk_space", snapshot.ErrorCode);
    CheckLong("disk failure available bytes", 512, snapshot.ErrorAvailableBytes);
    CheckLong("disk failure required bytes", 2048, snapshot.ErrorRequiredBytes);
  }

  private static void RunCancellationTests()
  {
    DownloadJob queued = new("tuf", "12345", "https://example.com/level.zip", "tuf-12345", false);
    CheckTrue("queued job cancellation succeeds", queued.TryCancel());
    CheckTrue("queued job cancellation requests token", queued.Token.IsCancellationRequested);
    CheckFalse("cancelled job cannot be cancelled twice", queued.TryCancel());

    DownloadJobSnapshot cancelled = queued.Snapshot();
    CheckString("cancelled job status", "cancelled", cancelled.Status);
    CheckString("cancelled job stage", "cancelled", cancelled.Stage);
    CheckTrue("cancelled job is terminal", cancelled.Done);

    LevelDownloadResult result = new()
    {
      SourceUrl = "https://example.com/level.zip",
      DirectUrl = "https://cdn.example.com/level.zip",
      Directory = "/tmp/tuf-12345",
      SelectedLevelPath = "/tmp/tuf-12345/chart.adofai",
      LevelPaths = new List<string> { "/tmp/tuf-12345/chart.adofai" }
    };

    queued.Report("downloading", "Downloading level archive", 0.5, 50, 100);
    queued.Complete(result, false);
    queued.WaitForSelection(result);
    queued.Fail(new InvalidOperationException("late failure"));
    CheckString("late callbacks preserve cancellation", "cancelled", queued.Snapshot().Status);

    DownloadJob completed = new("tuf", "54321", "https://example.com/complete.zip", "tuf-54321", false);
    completed.Complete(result, false);
    CheckFalse("completed job cannot be cancelled", completed.TryCancel());
    CheckFalse("completed job token remains active", completed.Token.IsCancellationRequested);
    CheckString("completed status is preserved", "completed", completed.Snapshot().Status);
  }

  private static void RunDownloadStorageMigrationTests()
  {
    string installRoot = Path.Combine(Path.GetTempPath(), "tufhelperlite-storage-tests-" + Guid.NewGuid().ToString("N"));
    string targetRoot = Path.Combine(installRoot, "new-downloads");
    Directory.CreateDirectory(installRoot);
    Directory.CreateDirectory(targetRoot);

    try
    {
      DownloadStorageSettingsStore.Initialize(installRoot);
      DownloadStorageMigrationService.Initialize(installRoot);
      DownloadStorageMigrationService.SetLevelInUseProbeForTests(_ => false);
      string sourceRoot = DownloadStorageSettingsStore.GetDownloadRoot();
      string sourceLevel = CreateLevel(sourceRoot, "tuf-777", "nested/chart.adofai");
      string sourceAudio = Path.Combine(sourceRoot, "tuf-777", "song.ogg");
      File.WriteAllText(sourceAudio, "audio");

      CheckString("default storage root", Path.GetFullPath(Path.Combine(installRoot, "Downloads")), sourceRoot);
      CheckTrue("new target validates", DownloadStorageMigrationService.ValidateSelectedTarget(targetRoot) == Path.GetFullPath(targetRoot));

      DownloadStorageMigrationSnapshot started = DownloadStorageMigrationService.StartForTarget(targetRoot);
      CheckString("migration starts copying", "copying", started.State);
      CheckTrue("migration worker completes", DownloadStorageMigrationService.WaitForWorkerForTests());

      DownloadStorageMigrationSnapshot completed = DownloadStorageMigrationService.GetStatus();
      CheckString("migration completed", "completed", completed.State);
      CheckString("new storage root active", Path.GetFullPath(targetRoot), DownloadStorageSettingsStore.GetDownloadRoot());
      CheckFalse("old storage removed", Directory.Exists(sourceRoot));
      string movedLevel = Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, sourceLevel));
      CheckTrue("level moved", File.Exists(movedLevel));
      CheckTrue("supporting file moved", File.Exists(Path.Combine(targetRoot, "tuf-777", "song.ogg")));
      Check("resolver uses moved root", 777, LevelContextResolver.ResolveTufLevelId(movedLevel));

      string inUseTarget = Path.Combine(installRoot, "in-use-target");
      Directory.CreateDirectory(inUseTarget);
      DownloadStorageMigrationService.SetLevelInUseProbeForTests(_ => true);
      DownloadStorageMigrationSnapshot inUse = DownloadStorageMigrationService.StartForTarget(inUseTarget);
      CheckString("open downloaded level blocks migration", "downloaded_level_in_use", inUse.ErrorCode);
      DownloadStorageMigrationService.SetLevelInUseProbeForTests(_ => false);

      string nonEmpty = Path.Combine(installRoot, "non-empty");
      Directory.CreateDirectory(nonEmpty);
      File.WriteAllText(Path.Combine(nonEmpty, "keep.txt"), "keep");
      try
      {
        DownloadStorageMigrationService.ValidateSelectedTarget(nonEmpty);
        Failures.Add("non-empty target rejected: expected exception");
      }
      catch (DownloadStorageMigrationException exception)
      {
        CheckString("non-empty target error", "storage_target_not_empty", exception.Code);
      }

      try
      {
        DownloadStorageMigrationService.ValidateSelectedTarget(Path.Combine(targetRoot, "nested"), true);
        Failures.Add("nested target rejected: expected exception");
      }
      catch (DownloadStorageMigrationException exception)
      {
        CheckString("nested target error", "storage_target_overlaps_source", exception.Code);
      }

      RunDownloadStorageResumeTest(installRoot);
      RunCorruptStorageSettingsTest(installRoot);
    }
    finally
    {
      DownloadStorageSettingsStore.Initialize(AppDomain.CurrentDomain.BaseDirectory);
      DownloadStorageMigrationService.Initialize(AppDomain.CurrentDomain.BaseDirectory);
      DownloadStorageMigrationService.SetLevelInUseProbeForTests(null);
      if (Directory.Exists(installRoot)) Directory.Delete(installRoot, true);
    }
  }

  private static void RunDownloadStorageResumeTest(string testRoot)
  {
    string installRoot = Path.Combine(testRoot, "resume-install");
    string sourceRoot = Path.Combine(installRoot, "Downloads");
    string targetRoot = Path.Combine(testRoot, "resume-target");
    Directory.CreateDirectory(installRoot);
    Directory.CreateDirectory(targetRoot);
    string sourceLevel = CreateLevel(sourceRoot, "tuf-888", "chart.adofai");

    DownloadStorageSettingsStore.Initialize(installRoot);
    File.WriteAllText(
      Path.Combine(installRoot, "DownloadMigration.json"),
      JsonConvert.SerializeObject(new DownloadStorageMigrationSnapshot
      {
        OperationId = Guid.NewGuid().ToString("N"),
        State = "copying",
        SourceDirectory = sourceRoot,
        TargetDirectory = targetRoot,
        Message = "Interrupted test migration"
      })
    );

    DownloadStorageMigrationService.Initialize(installRoot);
    CheckTrue("resumed migration worker completes", DownloadStorageMigrationService.WaitForWorkerForTests());
    DownloadStorageMigrationSnapshot resumed = DownloadStorageMigrationService.GetStatus();
    CheckString("interrupted migration resumes", "completed", resumed.State);
    CheckString("resumed target becomes active", Path.GetFullPath(targetRoot), DownloadStorageSettingsStore.GetDownloadRoot());
    CheckFalse("resumed source removed", Directory.Exists(sourceRoot));
    CheckTrue("resumed level copied", File.Exists(Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, sourceLevel))));
  }

  private static void RunCorruptStorageSettingsTest(string testRoot)
  {
    string installRoot = Path.Combine(testRoot, "corrupt-settings-install");
    Directory.CreateDirectory(installRoot);
    File.WriteAllText(Path.Combine(installRoot, "Settings.json"), "{not-json");
    DownloadStorageSettingsStore.Initialize(installRoot);
    CheckString(
      "corrupt settings fall back to default",
      Path.GetFullPath(Path.Combine(installRoot, "Downloads")),
      DownloadStorageSettingsStore.GetDownloadRoot());
  }

  private static void RunDownloadLibraryTests()
  {
    string installRoot = Path.Combine(Path.GetTempPath(), "tufhelperlite-library-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(installRoot);
    try
    {
      DownloadStorageSettingsStore.Initialize(installRoot);
      DownloadLibraryService.Initialize(installRoot);
      DownloadLibraryService.SetMetadataProviderForTests(id => new TUFHelperLite.Infrastructure.Tuforums.TufLevelInfo
      {
        Id = int.Parse(id),
        DiffId = 12,
        Song = "Fetched Level " + id,
        Artist = "Fetched Artist " + id,
        Creator = "Fetched Creator " + id
      });
      string downloadRoot = DownloadStorageSettingsStore.GetDownloadRoot();
      const long baseTimestamp = 1_700_000_000_000;
      for (int id = 1; id <= 65; id++)
      {
        string directory = Path.Combine(downloadRoot, "tuf-" + id);
        CreateLevel(downloadRoot, "tuf-" + id, "chart.adofai");
        File.WriteAllText(Path.Combine(directory, "payload.bin"), new string('x', id));
        File.WriteAllText(
          Path.Combine(directory, DownloadLibraryService.ManifestFileName),
          JsonConvert.SerializeObject(new
          {
            Version = 1,
            Id = id,
            DiffId = id % 30,
            Artist = "Artist " + id,
            LevelName = "Level " + id,
            Creator = "Creator " + id,
            SizeBytes = id + 2L,
            DownloadedAtUnixMs = baseTimestamp + (id == 64 ? 65 : id),
            MetadataState = id == 1 ? "partial" : "ready"
          }));
      }

      DownloadLibraryService.RebuildSummaryForTests();
      DownloadLibrarySummary summary = DownloadLibraryService.GetSummary();
      CheckString("download library summary ready", "ready", summary.State);
      CheckLong("download library summary count", 65, summary.LevelCount);
      CheckLong("download library summary bytes", 2275, summary.TotalSizeBytes);
      CheckLong("download library candidate cap", 21, DownloadLibraryService.GetCandidateCapacityForTests(20));

      DownloadLibraryService.ResetCandidateCountForTests();
      DownloadedLevelPage first = DownloadLibraryService.GetPage(null, "next", 20);
      CheckLong("download library observed candidate bound", 21, DownloadLibraryService.MaximumCandidateCountObservedForTests);
      CheckLong("first library page size", 20, first.Items.Length);
      CheckLong("first library page newest id", 65, first.Items[0].Id);
      CheckLong("download library timestamp tie-break", 64, first.Items[1].Id);
      CheckLong("first library page last id", 46, first.Items[19].Id);
      CheckTrue("first library page has next", first.HasNext);
      CheckFalse("first library page has previous", first.HasPrevious);

      DownloadedLevelPage second = DownloadLibraryService.GetPage(first.NextCursor, "next", 20);
      CheckLong("second library page newest id", 45, second.Items[0].Id);
      CheckLong("second library page last id", 26, second.Items[19].Id);
      CheckTrue("second library page has previous", second.HasPrevious);

      DownloadedLevelPage third = DownloadLibraryService.GetPage(second.NextCursor, "next", 20);
      DownloadedLevelPage fourth = DownloadLibraryService.GetPage(third.NextCursor, "next", 20);
      CheckLong("last library page size", 5, fourth.Items.Length);
      CheckLong("last library page final id", 1, fourth.Items[4].Id);
      CheckString("partial metadata is fetched before response", "ready", fourth.Items[4].MetadataState);
      CheckString("fetched level name returned", "Fetched Level 1", fourth.Items[4].LevelName);
      CheckLong("fetched difficulty returned", 12, fourth.Items[4].DiffId);
      CheckFalse("last library page has next", fourth.HasNext);

      DownloadedLevelPage previous = DownloadLibraryService.GetPage(fourth.PreviousCursor, "previous", 20);
      CheckLong("previous library page newest id", 25, previous.Items[0].Id);
      CheckLong("previous library page last id", 6, previous.Items[19].Id);

      string newDirectory = Path.Combine(downloadRoot, "tuf-66");
      string newLevel = CreateLevel(downloadRoot, "tuf-66", "chart.adofai");
      LevelDownloadResult result = new()
      {
        Directory = newDirectory,
        SelectedLevelPath = newLevel,
        LevelPaths = new List<string> { newLevel },
        FromCache = false
      };
      DownloadLibraryService.RecordDownload(result, null, "66");
      DownloadedLevelUpdateDescriptor descriptor = DownloadLibraryService.GetUpdateDescriptor(66);
      CheckTrue("download manifest records installed payload hash", !string.IsNullOrWhiteSpace(descriptor.InstalledPayloadHash));
      CheckString("legacy download revision remains unknown", null, descriptor.DownloadedFileId);
      TUFHelperLite.Infrastructure.Tuforums.TufLevelInfo updateMetadata = new()
      {
        Id = 66,
        DiffId = 18,
        Song = "Updated Level",
        Artist = "Updated Artist",
        Creator = "Updated Creator",
        FileId = "file-v2",
        UpdatedAt = "2026-08-19T00:00:00Z"
      };
      DownloadedLevelItem available = DownloadLibraryService.RecordUpdateCheck(
        66, updateMetadata, "candidate-hash", false);
      CheckString("available update persists in item", "update_available", available.UpdateState);
      CheckString("available update persists after reload", "update_available", DownloadLibraryService.GetItem(66).UpdateState);
      DownloadedLevelItem current = DownloadLibraryService.RecordUpdateCheck(66, updateMetadata, null, true);
      CheckString("matching update clears availability", "idle", current.UpdateState);
      CheckString("matching update records remote revision", "file-v2", DownloadLibraryService.GetUpdateDescriptor(66).DownloadedFileId);
      try
      {
        DownloadLibraryService.GetPage(first.NextCursor, "next", 20);
        Failures.Add("stale download library cursor rejected: expected exception");
      }
      catch (InvalidOperationException exception)
      {
        CheckString("stale download library cursor error", "download_library_cursor_stale", exception.Message);
      }
    }
    finally
    {
      DownloadStorageSettingsStore.Initialize(AppDomain.CurrentDomain.BaseDirectory);
      DownloadLibraryService.Initialize(AppDomain.CurrentDomain.BaseDirectory);
      DownloadLibraryService.SetMetadataProviderForTests(null);
      if (Directory.Exists(installRoot)) Directory.Delete(installRoot, true);
    }
  }

  private static void RunLevelUpdateTests()
  {
    string installRoot = Path.Combine(Path.GetTempPath(), "tufhelperlite-update-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(installRoot);
    try
    {
      DownloadStorageSettingsStore.Initialize(installRoot);
      DownloadLibraryService.Initialize(installRoot);
      DownloadStorageMigrationService.Initialize(installRoot);
      DownloadStorageMigrationService.SetLevelInUseProbeForTests(_ => false);
      LevelUpdateService.Initialize(installRoot);
      string root = DownloadCachePaths.GetDownloadRoot();
      string directory = Path.Combine(root, "tuf-100");
      string levelPath = CreateLevel(root, "tuf-100", "chart.adofai");
      File.WriteAllText(levelPath, "old payload");
      TUFHelperLite.Infrastructure.Tuforums.TufLevelInfo installed = new()
      {
        Id = 100,
        DiffId = 12,
        Song = "Old Level",
        Artist = "Artist",
        Creator = "Creator",
        FileId = "file-v1",
        DownloadLink = "https://cdn.example/file-v1.zip"
      };
      DownloadLibraryService.RecordDownload(new LevelDownloadResult
      {
        Directory = directory,
        SelectedLevelPath = levelPath,
        LevelPaths = new List<string> { levelPath },
        FromCache = false
      }, installed, "100");
      DownloadedLevelUpdateDescriptor before = DownloadLibraryService.GetUpdateDescriptor(100);
      TUFHelperLite.Infrastructure.Tuforums.TufLevelInfo available = new()
      {
        Id = 100,
        DiffId = 18,
        Song = "New Level",
        Artist = "Artist",
        Creator = "Creator",
        FileId = "file-v2",
        UpdatedAt = "2026-08-19T00:00:00Z",
        DownloadLink = "https://cdn.example/file-v2.zip"
      };
      int stagingDownloads = 0;
      LevelUpdateService.SetDependenciesForTests(
        _ => available,
        (url, staging, token, progress) =>
        {
          stagingDownloads++;
          Directory.CreateDirectory(staging);
          string stagedLevel = Path.Combine(staging, "chart.adofai");
          File.WriteAllText(stagedLevel, "new payload with a different size");
          progress?.Invoke(new LevelDownloadProgress { Stage = "downloading", Progress = 1 });
          return new LevelDownloadResult
          {
            SourceUrl = url,
            DirectUrl = url,
            Directory = staging,
            SelectedLevelPath = stagedLevel,
            LevelPaths = new List<string> { stagedLevel },
            FromCache = false
          };
        });

      DownloadJob checkJob = new("level.update-check", "100", null, "tuf-100", false);
      LevelUpdateService.Check(100, checkJob);
      CheckString("known revision update check state", "update_available", checkJob.Snapshot().UpdateState);
      CheckLong("known revision avoids payload download", 0, stagingDownloads);
      CheckString("update availability survives manifest reload", "update_available", DownloadLibraryService.GetItem(100).UpdateState);

      DownloadLibraryService.RebuildSummaryForTests();
      DownloadLibrarySummary summaryBefore = DownloadLibraryService.GetSummary();
      DownloadJob updateJob = new("level.update", "100", null, "tuf-100", false);
      LevelUpdateService.Update(100, updateJob);
      DownloadJobSnapshot completed = updateJob.Snapshot();
      DownloadedLevelUpdateDescriptor after = DownloadLibraryService.GetUpdateDescriptor(100);
      DownloadLibrarySummary summaryAfter = DownloadLibraryService.GetSummary();
      CheckString("level update job completes up to date", "up_to_date", completed.UpdateState);
      CheckLong("level update downloads staged payload once", 1, stagingDownloads);
      CheckString("level update activates new payload", "new payload with a different size", File.ReadAllText(Path.Combine(directory, "chart.adofai")));
      CheckString("level update records new revision", "file-v2", after.DownloadedFileId);
      CheckLong("level update preserves downloaded timestamp", before.DownloadedAtUnixMs, after.DownloadedAtUnixMs);
      CheckLong("level update preserves summary count", summaryBefore.LevelCount, summaryAfter.LevelCount);
      CheckLong("level update adjusts summary size", after.SizeBytes, summaryAfter.TotalSizeBytes);

      string legacyDirectory = Path.Combine(root, "tuf-101");
      string legacyLevel = CreateLevel(root, "tuf-101", "chart.adofai");
      File.WriteAllText(legacyLevel, "legacy payload");
      DownloadLibraryService.RecordDownload(new LevelDownloadResult
      {
        Directory = legacyDirectory,
        SelectedLevelPath = legacyLevel,
        LevelPaths = new List<string> { legacyLevel },
        FromCache = true
      }, installed, "101");
      TUFHelperLite.Infrastructure.Tuforums.TufLevelInfo legacyRemote = new()
      {
        Id = 101,
        DiffId = 12,
        Song = "Legacy Level",
        Artist = "Artist",
        Creator = "Creator",
        FileId = "legacy-file-v2",
        DownloadLink = "https://cdn.example/101.zip"
      };
      LevelUpdateService.SetDependenciesForTests(
        _ => legacyRemote,
        (url, staging, token, progress) =>
        {
          stagingDownloads++;
          Directory.CreateDirectory(staging);
          string stagedLevel = Path.Combine(staging, "chart.adofai");
          File.WriteAllText(stagedLevel, "legacy payload");
          return new LevelDownloadResult
          {
            SourceUrl = url,
            DirectUrl = url,
            Directory = staging,
            SelectedLevelPath = stagedLevel,
            LevelPaths = new List<string> { stagedLevel },
            FromCache = false
          };
        });
      DownloadJob legacyCheck = new("level.update-check", "101", null, "tuf-101", false);
      LevelUpdateService.Check(101, legacyCheck);
      CheckString("unknown revision compares equal payload", "up_to_date", legacyCheck.Snapshot().UpdateState);
      CheckString("equal legacy payload records remote revision", "legacy-file-v2", DownloadLibraryService.GetUpdateDescriptor(101).DownloadedFileId);

      string changedDirectory = Path.Combine(root, "tuf-102");
      string changedLevel = CreateLevel(root, "tuf-102", "chart.adofai");
      File.WriteAllText(changedLevel, "old legacy payload");
      DownloadLibraryService.RecordDownload(new LevelDownloadResult
      {
        Directory = changedDirectory,
        SelectedLevelPath = changedLevel,
        LevelPaths = new List<string> { changedLevel },
        FromCache = true
      }, installed, "102");
      legacyRemote.Id = 102;
      legacyRemote.FileId = "changed-file-v2";
      legacyRemote.DownloadLink = "https://cdn.example/102.zip";
      DownloadJob changedCheck = new("level.update-check", "102", null, "tuf-102", false);
      LevelUpdateService.Check(102, changedCheck);
      CheckString("unknown revision detects changed payload", "update_available", changedCheck.Snapshot().UpdateState);
      CheckString("changed legacy availability persists", "update_available", DownloadLibraryService.GetItem(102).UpdateState);

      DownloadStorageMigrationService.SetLevelInUseProbeForTests(_ => true);
      try
      {
        LevelUpdateService.Update(102, new DownloadJob("level.update", "102", null, "tuf-102", false));
        Failures.Add("open downloaded level update rejected: expected exception");
      }
      catch (LevelUpdateException exception)
      {
        CheckString("open downloaded level update error", "downloaded_level_in_use", exception.Code);
      }
      finally
      {
        DownloadStorageMigrationService.SetLevelInUseProbeForTests(_ => false);
      }

      string recoveryTarget = Path.Combine(root, ".update-recovery-target");
      string recoveryBackup = recoveryTarget + ".backup";
      string recoveryStaging = recoveryTarget + ".staging";
      Directory.CreateDirectory(recoveryTarget);
      Directory.CreateDirectory(recoveryBackup);
      Directory.CreateDirectory(recoveryStaging);
      File.WriteAllText(Path.Combine(recoveryTarget, "state.txt"), "new");
      File.WriteAllText(Path.Combine(recoveryBackup, "state.txt"), "old");
      File.WriteAllText(Path.Combine(installRoot, "DownloadUpdateJournal.json"), JsonConvert.SerializeObject(new
      {
        Version = 1,
        Id = 100,
        TargetDirectory = recoveryTarget,
        StagingDirectory = recoveryStaging,
        BackupDirectory = recoveryBackup,
        Phase = "backup_moved"
      }));
      LevelUpdateService.Initialize(installRoot);
      CheckString("interrupted activation restores old payload", "old", File.ReadAllText(Path.Combine(recoveryTarget, "state.txt")));
      CheckFalse("interrupted activation removes backup", Directory.Exists(recoveryBackup));
      CheckFalse("interrupted activation clears journal", File.Exists(Path.Combine(installRoot, "DownloadUpdateJournal.json")));
    }
    finally
    {
      LevelUpdateService.SetDependenciesForTests(null, null);
      DownloadStorageMigrationService.SetLevelInUseProbeForTests(null);
      DownloadStorageSettingsStore.Initialize(AppDomain.CurrentDomain.BaseDirectory);
      DownloadLibraryService.Initialize(AppDomain.CurrentDomain.BaseDirectory);
      if (Directory.Exists(installRoot)) Directory.Delete(installRoot, true);
    }
  }

  private static void RunAdofaiIpcMigrationGuardTests()
  {
    CheckTrue("legacy direct entrypoint requires migration",
      AdofaiIpcMigrationBridge.RequiresLegacyMigration("TUFHelperLite.Main.Load"));
    CheckFalse("dependency entrypoint skips legacy migration",
      AdofaiIpcMigrationBridge.RequiresLegacyMigration("TUFHelperLite.Launcher.DependencyEntryPoint.Load"));
    CheckFalse("dependency shim entrypoint skips legacy migration",
      AdofaiIpcMigrationBridge.RequiresLegacyMigration("AdofaiIpc.DependencyShim.DependencyShim.Load"));
    CheckFalse("missing entrypoint skips legacy migration",
      AdofaiIpcMigrationBridge.RequiresLegacyMigration(null));
  }

  private static void Check(string name, int? expected, int? actual)
  {
    if (expected != actual) Failures.Add(name + ": expected " + expected + ", got " + actual);
  }

  private static void CheckLong(string name, long expected, long actual)
  {
    if (expected != actual) Failures.Add(name + ": expected " + expected + ", got " + actual);
  }

  private static void CheckString(string name, string expected, string actual)
  {
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
      Failures.Add(name + ": expected " + expected + ", got " + actual);
  }

  private static void CheckTrue(string name, bool actual)
  {
    if (!actual) Failures.Add(name + ": expected true");
  }

  private static void CheckFalse(string name, bool actual)
  {
    if (actual) Failures.Add(name + ": expected false");
  }
}
