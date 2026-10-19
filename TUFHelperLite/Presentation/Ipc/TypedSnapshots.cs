using System;
using System.Linq;
using AdofaiIpc;
using TUFHelperLite.App;
using TUFHelperLite.Domain.Downloads;
using TUFHelperLite.Domain.Jobs;
using TUFHelperLite.Domain.Levels;
using TUFHelperLite.Domain.Storage;
using Wire = TUFHelperLite.Presentation.Ipc.Generated;

namespace TUFHelperLite.Presentation.Ipc;

internal static class TypedSnapshots
{
  internal static Wire.Settings Settings(DownloadSettingsSnapshot s) => new() {
    TempDownloadMode = s.TempDownloadMode, TempDirectory = s.TempDirectory
  };
  internal static Wire.Job Job(DownloadJobSnapshot s) => new Wire.Job {
    JobId = s.JobId, Kind = s.Kind, Status = s.Status switch {
      "queued" => Wire.JobStatus.Queued, "running" => Wire.JobStatus.Running,
      "waiting_selection" => Wire.JobStatus.WaitingSelection, "completed" => Wire.JobStatus.Completed,
      "failed" => Wire.JobStatus.Failed, "cancelled" => Wire.JobStatus.Cancelled,
      _ => throw new IpcCallException("invalid_result", "Unknown job status: " + s.Status)
    },
    Stage = s.Stage, Message = s.Message, CacheKey = s.CacheKey, QueuePosition = s.QueuePosition,
    Progress = s.Progress, BytesReceived = s.BytesReceived, TotalBytes = s.TotalBytes,
    LevelId = s.LevelId, Song = s.Song, Artist = s.Artist, Creator = s.Creator, DifficultyId = s.DifficultyId,
    SizeBytes = s.SizeBytes, UpdateState = s.UpdateState, UpdateStateExpiresAtUtc = s.UpdateStateExpiresAtUtc,
    InstalledFileId = s.InstalledFileId, AvailableFileId = s.AvailableFileId, AvailableUpdatedAtUtc = s.AvailableUpdatedAtUtc,
    SourceUrl = s.SourceUrl, DirectUrl = s.DirectUrl, Directory = s.Directory, SelectedLevelPath = s.SelectedLevelPath,
    LevelPaths = s.LevelPaths ?? Array.Empty<string>(), OpenAfterDownload = s.OpenAfterDownload,
    WaitingForSelection = s.WaitingForSelection, Opened = s.Opened, FromCache = s.FromCache,
    Error = s.Error, ErrorCode = s.ErrorCode, ErrorAvailableBytes = s.ErrorAvailableBytes,
    ErrorRequiredBytes = s.ErrorRequiredBytes, CreatedAtUnixMs = s.CreatedAtUnixMs,
    UpdatedAtUnixMs = s.UpdatedAtUnixMs, Done = s.Done
  };
  internal static Wire.Job[] Jobs(DownloadJobSnapshot[] jobs) => jobs.Select(Job).ToArray();
  internal static Wire.LibraryItem Item(DownloadedLevelItem s) => new Wire.LibraryItem {
    Id = s.Id, DiffId = s.DiffId, Artist = s.Artist, LevelName = s.LevelName, Creator = s.Creator,
    SizeBytes = s.SizeBytes, DownloadedAtUnixMs = s.DownloadedAtUnixMs, DownloadedAtUtc = s.DownloadedAtUtc,
    MetadataState = s.MetadataState, UpdateState = s.UpdateState, UpdateStateExpiresAtUtc = s.UpdateStateExpiresAtUtc
  };
  internal static Wire.LibraryPage Page(DownloadedLevelPage s) => new Wire.LibraryPage {
    Revision = s.Revision, StartIndex = s.StartIndex, TotalCount = s.TotalCount,
    Items = (s.Items ?? Array.Empty<DownloadedLevelItem>()).Select(Item).ToArray(),
    NextCursor = s.NextCursor, PreviousCursor = s.PreviousCursor, HasNext = s.HasNext, HasPrevious = s.HasPrevious
  };
  internal static Wire.LibrarySummary Summary(DownloadLibrarySummary s) => s == null ? null : new Wire.LibrarySummary {
    State = s.State, Revision = s.Revision, LevelCount = s.LevelCount, TotalSizeBytes = s.TotalSizeBytes,
    LastCalculatedAtUtc = s.LastCalculatedAtUtc, ErrorCode = s.ErrorCode, Message = s.Message
  };
  internal static Wire.Storage Storage(DownloadStorageMigrationSnapshot s) => new Wire.Storage {
    OperationId = s.OperationId, OperationKind = s.OperationKind, SelectionKind = s.SelectionKind, State = s.State,
    SourceDirectory = s.SourceDirectory, TargetDirectory = s.TargetDirectory, CurrentDirectory = s.CurrentDirectory,
    FilesProcessed = s.FilesProcessed, FilesTotal = s.FilesTotal, BytesProcessed = s.BytesProcessed, BytesTotal = s.BytesTotal,
    ErrorCode = s.ErrorCode, Message = s.Message, IsDefault = s.IsDefault, DefaultDirectory = s.DefaultDirectory,
    CurrentLevelId = s.CurrentLevelId, Phase = s.Phase
  };
  internal static Wire.Batch Batch(LevelUpdateCheckBatchSnapshot s) => new Wire.Batch {
    State = s.State, OperationKind = s.OperationKind, OperationId = s.OperationId, CurrentLevelId = s.CurrentLevelId,
    CurrentStage = s.CurrentStage, CurrentProgress = s.CurrentProgress, LevelsProcessed = s.LevelsProcessed,
    LevelsTotal = s.LevelsTotal, UpdatesAvailable = s.UpdatesAvailable, LevelsUpToDate = s.LevelsUpToDate,
    LevelsUpdated = s.LevelsUpdated, LevelsFailed = s.LevelsFailed, ErrorCode = s.ErrorCode, Message = s.Message
  };
  internal static Wire.FolderSelection Folder(DownloadFolderPickerSnapshot s) => new Wire.FolderSelection {
    OperationId = s.OperationId, State = s.State, SelectionToken = s.SelectionToken, Directory = s.Directory,
    SelectionKind = s.SelectionKind, ErrorCode = s.ErrorCode, Message = s.Message
  };
}
