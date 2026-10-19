import "@adofai-ipc/typespec";

@ipc.mod("tufhelperlite")
namespace TufHelper {
  enum JobStatus {
    Queued: "queued", Running: "running", WaitingSelection: "waiting_selection",
    Completed: "completed", Failed: "failed", Cancelled: "cancelled"
  }
  enum PageDirection { Next: "next", Previous: "previous" }

  model Health {
    ok: boolean;
    `mod`: string;
    version: string;
    capabilities: string[];
  }
  model Job {
    jobId: string;
    kind: string | null;
    status: JobStatus;
    stage: string | null;
    message: string | null;
    cacheKey: string | null;
    queuePosition: int32;
    progress: float64;
    bytesReceived: safeint;
    totalBytes: safeint;
    levelId: string | null;
    song: string | null;
    artist: string | null;
    creator: string | null;
    difficultyId: int32;
    sizeBytes: safeint;
    updateState: string | null;
    updateStateExpiresAtUtc: string | null;
    installedFileId: string | null;
    availableFileId: string | null;
    availableUpdatedAtUtc: string | null;
    sourceUrl: string | null;
    directUrl: string | null;
    directory: string | null;
    selectedLevelPath: string | null;
    levelPaths: string[];
    openAfterDownload: boolean;
    waitingForSelection: boolean;
    opened: boolean;
    fromCache: boolean;
    error: string | null;
    errorCode: string | null;
    errorAvailableBytes: safeint;
    errorRequiredBytes: safeint;
    createdAtUnixMs: safeint;
    updatedAtUnixMs: safeint;
    done: boolean;
  }
  model LibraryItem {
    id: int32;
    diffId: int32;
    artist: string | null;
    levelName: string | null;
    creator: string | null;
    sizeBytes: safeint;
    downloadedAtUnixMs: safeint;
    downloadedAtUtc: string | null;
    metadataState: string | null;
    updateState: string | null;
    updateStateExpiresAtUtc: string | null;
  }
  model LibraryPage {
    revision: safeint;
    startIndex: int32;
    totalCount: int32;
    items: LibraryItem[];
    nextCursor: string | null;
    previousCursor: string | null;
    hasNext: boolean;
    hasPrevious: boolean;
  }
  model LibrarySummary {
    state: string;
    revision: safeint;
    levelCount: int32;
    totalSizeBytes: safeint;
    lastCalculatedAtUtc: string | null;
    errorCode: string | null;
    message: string | null;
  }
  model Storage {
    operationId: string | null;
    operationKind: string | null;
    selectionKind: string | null;
    state: string;
    sourceDirectory: string | null;
    targetDirectory: string | null;
    currentDirectory: string | null;
    filesProcessed: int32;
    filesTotal: int32;
    bytesProcessed: safeint;
    bytesTotal: safeint;
    errorCode: string | null;
    message: string | null;
    isDefault: boolean;
    defaultDirectory: string | null;
    currentLevelId: string | null;
    phase: string | null;
  }
  model Batch {
    state: string;
    operationKind: string | null;
    operationId: string | null;
    currentLevelId: string | null;
    currentStage: string | null;
    currentProgress: float64;
    levelsProcessed: int32;
    levelsTotal: int32;
    updatesAvailable: int32;
    levelsUpToDate: int32;
    levelsUpdated: int32;
    levelsFailed: int32;
    errorCode: string | null;
    message: string | null;
  }
  model FolderSelection {
    operationId: string;
    state: string;
    selectionToken: string | null;
    directory: string | null;
    selectionKind: string | null;
    errorCode: string | null;
    message: string | null;
  }
  model Snapshot {
    health: Health;
    jobs: Job[];
    levelIds: string[];
    storage: Storage;
    batch: Batch;
    summary: LibrarySummary | null;
  }
  model Membership { levelIds: string[]; }
  model CancelResult { ok: boolean; jobId: string; cancelled: boolean; }
  model SelectResult { ok: boolean; jobId: string; levelPath: string; opened: boolean; }

  op snapshotRefresh(): Snapshot;
  op openFromId(id: string, openAfterDownload?: boolean, source?: string | null): Job;
  op openFromUrl(url: string, openAfterDownload?: boolean, source?: string | null): Job;
  op download(url: string, levelId?: string | null, source?: string | null): Job;
  op watchLibrary(ids: string[]): Membership;
  op libraryPage(cursor?: string | null, direction?: PageDirection, limit?: int32): LibraryPage;
  op checkUpdate(id: string): Job;
  op updateLevel(id: string): Job;
  op checkAll(): Batch;
  op cancelCheckAll(): Batch;
  op updateAll(): Batch;
  op cancelUpdateAll(): Batch;
  op cancelJob(jobId: string): CancelResult;
  op selectLevel(jobId: string, levelPath: string): SelectResult;
  op pickFolder(allowExisting?: boolean): FolderSelection;
  op migrateStorage(selectionToken?: string | null, useDefault?: boolean): Storage;
  op retryMigration(): Storage;
  op changeStorage(selectionToken?: string | null, useDefault?: boolean): Storage;
  op retryChange(): Storage;
  op cancelChange(): Storage;

  @ipc.app
  interface Browser {
    snapshot(state: Snapshot): void;
    jobsChanged(jobs: Job[]): void;
    libraryChanged(summary: LibrarySummary | null): void;
    membershipChanged(levelIds: string[]): void;
    storageChanged(storage: Storage): void;
    batchChanged(batch: Batch): void;
    folderSelection(selection: FolderSelection): void;
  }
}
