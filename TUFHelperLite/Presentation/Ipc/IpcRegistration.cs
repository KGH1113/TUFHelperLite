using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdofaiIpc;
using AdofaiIpc.Contracts;
using TUFHelperLite.App;
using TUFHelperLite.Domain.Ports;
using TUFHelperLite.Infrastructure.Downloads;
using Wire = TUFHelperLite.Presentation.Ipc.Generated;

namespace TUFHelperLite.Presentation.Ipc;

public static class IpcRegistration
{
  private static ModRegistration<Wire.TufHelperApp> _mod;
  private static readonly ActivityChangeBuffer Changes = new();
  private static readonly object PublisherGate = new();
  private static readonly Dictionary<string, AppNotifications> Apps = new();
  private static readonly Dictionary<string, string> FolderOwners = new();
  private static readonly Dictionary<string, string> SelectionOwners = new();
  private static readonly Dictionary<string, string[]> LibraryInterests = new();
  private static System.Threading.Timer _publisher;
  private static volatile bool _ready;
  private static int _publishing;

  private sealed class MainThreadDispatcher : TUFHelperLite.App.Ports.IMainThreadDispatcher
  {
    public void Dispatch(Action action) => IpcRuntime.Current.RunOnGameThread(action);
  }

  public static void Register() => Register(IpcRuntime.Current, configureNativePicker: true);

  internal static void Register(IIpcRuntime runtime, bool configureNativePicker)
  {
    _mod = runtime.RegisterMod(Wire.TufHelperContract.Instance, ModStatus.DisplayName, new[] {
      "https://tuforums.com", "http://localhost:5173", "http://127.0.0.1:5173",
      "https://guhyeons-macbook-pro.tail234c02.ts.net"
    });
    RegisterHandlers(_mod);
    _mod.AppConnected += AppConnected;
    _mod.AppDisconnected += AppDisconnected;
    _mod.AppEventFailed += error => Main.Instance?.LogException(error);
    ActivityChanges.Sink = Changes;
    if (configureNativePicker)
    {
      DownloadFolderPickerCoordinator.MainThread = new MainThreadDispatcher();
      DownloadFolderPickerCoordinator.FolderPicker = new TUFHelperLite.Presentation.Unity.NativeFolderPicker();
    }
    _publisher = new System.Threading.Timer(_ => PublishChanges(), null, 100, 100);
    ModStatus.SetNormal();
  }

  public static void MarkReady()
  {
    _ready = true;
    _mod.MarkReady();
  }

  public static void MarkError(Exception exception) => _mod?.MarkError(
    "tufhelperlite_initialization_failed", exception?.Message ?? "TUFHelperLite initialization failed.");

  public static void Unregister()
  {
    try
    {
      _ready = false;
      ActivityChanges.Sink = null;
      _publisher?.Dispose();
      _publisher = null;
      lock (PublisherGate)
      {
        foreach (AppNotifications app in Apps.Values) app.Dispose();
        Apps.Clear(); FolderOwners.Clear(); SelectionOwners.Clear(); LibraryInterests.Clear();
      }
      _mod?.Dispose();
      _mod = null;
      Changes.Drain();
    }
    catch (Exception error) { Main.Instance?.LogException(error); }
  }

  private static void Bind<TRequest, TResult>(ModRegistration<Wire.TufHelperApp> mod,
    IpcMethod<TRequest, TResult> method, Func<TRequest, ModCallContext<Wire.TufHelperApp>, TResult> handler)
  {
    mod.Handle(method, (request, context) => {
      context.CancellationToken.ThrowIfCancellationRequested();
      try { return Task.FromResult(handler(request, context)); }
      catch (TUFHelperLite.Domain.Errors.LevelUpdateException error) { throw new IpcCallException(error.Code, error.Message); }
      catch (DownloadStorageMigrationException error) { throw new IpcCallException(error.Code, error.Message); }
    });
  }

  private static void RegisterHandlers(ModRegistration<Wire.TufHelperApp> mod)
  {
    Bind(mod, Wire.TufHelperMethods.SnapshotRefresh, (_, _) => Snapshot());
    Bind(mod, Wire.TufHelperMethods.OpenFromId, (request, _) => TypedSnapshots.Job(
      LevelJobService.StartOpenFromId(request.Id, !request.OpenAfterDownload.IsSpecified || request.OpenAfterDownload.Value)));
    Bind(mod, Wire.TufHelperMethods.OpenFromUrl, (request, _) => TypedSnapshots.Job(
      LevelJobService.StartOpenFromUrl(request.Url, !request.OpenAfterDownload.IsSpecified || request.OpenAfterDownload.Value)));
    Bind(mod, Wire.TufHelperMethods.Download, (request, _) => TypedSnapshots.Job(
      LevelJobService.StartDownload(request.Url, request.LevelId.IsSpecified ? request.LevelId.Value : null)));
    Bind(mod, Wire.TufHelperMethods.WatchLibrary, (request, context) => {
      string[] ids = request.Ids;
      if (ids.Length > 500 || ids.Any(id => !int.TryParse(id, out int number) || number <= 0))
        throw new IpcCallException("invalid_library_interest", "Choose at most 500 valid level IDs.");
      ids = ids.Distinct().ToArray();
      lock (PublisherGate)
      {
        context.CancellationToken.ThrowIfCancellationRequested();
        LibraryInterests[context.App.SessionId] = ids;
      }
      return new Wire.Membership { LevelIds = DownloadedMembership(ids) };
    });
    Bind(mod, Wire.TufHelperMethods.LibraryPage, (request, _) => {
      try
      {
        string direction = request.Direction.IsSpecified && request.Direction.Value == Wire.PageDirection.Previous ? "previous" : "next";
        return TypedSnapshots.Page(DownloadLibraryService.GetPage(
          request.Cursor.IsSpecified ? request.Cursor.Value : null, direction,
          request.Limit.IsSpecified ? request.Limit.Value : 0));
      }
      catch (InvalidOperationException error) when (error.Message == "download_library_cursor_stale")
      { throw new IpcCallException("download_library_cursor_stale", "The download library changed. Reload it to continue."); }
    });
    Bind(mod, Wire.TufHelperMethods.CheckUpdate, (request, _) => TypedSnapshots.Job(LevelJobService.StartUpdateCheck(request.Id)));
    Bind(mod, Wire.TufHelperMethods.UpdateLevel, (request, _) => TypedSnapshots.Job(LevelJobService.StartUpdate(request.Id)));
    Bind(mod, Wire.TufHelperMethods.CheckAll, (_, _) => TypedSnapshots.Batch(LevelUpdateCheckBatchService.Start()));
    Bind(mod, Wire.TufHelperMethods.CancelCheckAll, (_, _) => TypedSnapshots.Batch(LevelUpdateCheckBatchService.Cancel()));
    Bind(mod, Wire.TufHelperMethods.UpdateAll, (_, _) => TypedSnapshots.Batch(LevelUpdateCheckBatchService.StartUpdateAll()));
    Bind(mod, Wire.TufHelperMethods.CancelUpdateAll, (_, _) => TypedSnapshots.Batch(LevelUpdateCheckBatchService.Cancel()));
    Bind(mod, Wire.TufHelperMethods.CancelJob, (request, _) => {
      bool cancelled = LevelJobService.Cancel(request.JobId);
      return new Wire.CancelResult { Ok = cancelled, JobId = request.JobId, Cancelled = cancelled };
    });
    Bind(mod, Wire.TufHelperMethods.SelectLevel, (request, _) => {
      bool opened = LevelJobService.SelectLevel(request.JobId, request.LevelPath);
      return new Wire.SelectResult { Ok = opened, JobId = request.JobId, LevelPath = request.LevelPath, Opened = opened };
    });
    Bind(mod, Wire.TufHelperMethods.PickFolder, (request, context) => {
      DownloadFolderPickerSnapshot selection = DownloadFolderPickerCoordinator.Start(request.AllowExisting.IsSpecified && request.AllowExisting.Value);
      if (selection.State == "picking") lock (PublisherGate)
      {
        context.CancellationToken.ThrowIfCancellationRequested();
        FolderOwners[selection.OperationId] = context.App.SessionId;
      }
      Changes.Changed(ActivityTopic.Folder);
      return TypedSnapshots.Folder(selection);
    });
    Bind(mod, Wire.TufHelperMethods.MigrateStorage, (request, context) => {
      string token = request.SelectionToken.IsSpecified ? request.SelectionToken.Value : null;
      bool useDefault = request.UseDefault.IsSpecified && request.UseDefault.Value;
      ValidateSelection(token, useDefault, context.App.SessionId);
      return TypedSnapshots.Storage(DownloadStorageMigrationService.Start(token, useDefault));
    });
    Bind(mod, Wire.TufHelperMethods.RetryMigration, (_, _) => TypedSnapshots.Storage(DownloadStorageMigrationService.Retry()));
    Bind(mod, Wire.TufHelperMethods.ChangeStorage, (request, context) => {
      string token = request.SelectionToken.IsSpecified ? request.SelectionToken.Value : null;
      bool useDefault = request.UseDefault.IsSpecified && request.UseDefault.Value;
      ValidateSelection(token, useDefault, context.App.SessionId);
      return TypedSnapshots.Storage(DownloadStorageMigrationService.StartChange(token, useDefault));
    });
    Bind(mod, Wire.TufHelperMethods.RetryChange, (_, _) => TypedSnapshots.Storage(DownloadStorageMigrationService.Retry()));
    Bind(mod, Wire.TufHelperMethods.CancelChange, (_, _) => TypedSnapshots.Storage(DownloadStorageMigrationService.CancelChange()));
    Bind(mod, Wire.TufHelperMethods.SetTempDownloadMode, (request, _) => TypedSnapshots.Settings(DownloadSettingsService.SetTempDownloadMode(request.Enabled)));
    Bind(mod, Wire.TufHelperMethods.FlushTemp, (_, _) => TypedSnapshots.Settings(DownloadSettingsService.FlushTemp()));
    Bind(mod, Wire.TufHelperMethods.DeleteLevel, (request, _) => new Wire.DeleteLevelResult {
      Ok = true, Id = DownloadCachePaths.NormalizeLevelId(request.Id), Deleted = DownloadSettingsService.DeleteLevel(request.Id)
    });
  }

  private static void ValidateSelection(string token, bool useDefault, string sessionId)
  {
    if (useDefault) return;
    lock (PublisherGate)
    {
      if (string.IsNullOrEmpty(token) || !SelectionOwners.TryGetValue(token, out string owner) || owner != sessionId)
        throw new IpcCallException("selection_token_invalid", "Choose a download folder again to continue.");
      SelectionOwners.Remove(token);
    }
  }

  private static Wire.Snapshot Snapshot() => new Wire.Snapshot {
    Health = new Wire.Health {
      Ok = true, Mod = "TUFHelperLite", Version = ModStatus.Version,
      Capabilities = new[] {
        "download-storage-migration-v1", "downloaded-level-library-v1", "downloaded-level-positioned-pages-v1",
        "downloaded-level-update-v1", "downloaded-level-batch-update-check-v1",
        "downloaded-level-batch-update-v1", "download-storage-reconnect-v1",
        "temp-download-mode-v1", "downloaded-level-delete-v1"
      }
    },
    Jobs = TypedSnapshots.Jobs(LevelJobService.List()), LevelIds = Array.Empty<string>(),
    Storage = TypedSnapshots.Storage(DownloadStorageMigrationService.GetStatus()),
    Batch = TypedSnapshots.Batch(LevelUpdateCheckBatchService.GetStatus()), Summary = SafeSummary(),
    Settings = TypedSnapshots.Settings(DownloadSettingsService.GetStatus())
  };

  private static Wire.LibrarySummary SafeSummary()
  {
    try { return TypedSnapshots.Summary(DownloadLibraryService.GetSummary()); }
    catch { return null; } // Library reads can be temporarily blocked during storage cutover.
  }

  private static string[] DownloadedMembership(string[] ids)
  {
    if (DownloadSettingsService.GetStatus().TempDownloadMode) return Array.Empty<string>();
    string root = DownloadCachePaths.GetDownloadRoot();
    return ids.Where(id => {
      string path = Path.Combine(root, DownloadCachePaths.BuildTufCacheKey(id));
      return Directory.Exists(path) && Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .Any(file => string.Equals(Path.GetExtension(file), ".adofai", StringComparison.OrdinalIgnoreCase));
    }).ToArray();
  }

  private static void AppConnected(Wire.TufHelperApp app)
  {
    if (app.CancellationToken.IsCancellationRequested) return;
    lock (PublisherGate)
    {
      if (!_ready || app.CancellationToken.IsCancellationRequested) return;
      Wire.Snapshot snapshot = Snapshot();
      var queue = new AppNotifications(app);
      Apps.Add(app.SessionId, queue);
      queue.Enqueue("snapshot", () => app.SnapshotAsync(new Wire.SnapshotRequest { State = snapshot }, app.CancellationToken));
    }
  }

  private static void AppDisconnected(Wire.TufHelperApp app)
  {
    lock (PublisherGate)
    {
      if (Apps.TryGetValue(app.SessionId, out AppNotifications queue)) queue.Dispose();
      Apps.Remove(app.SessionId); LibraryInterests.Remove(app.SessionId);
      foreach (string token in SelectionOwners.Keys.ToArray()) if (SelectionOwners[token] == app.SessionId) SelectionOwners.Remove(token);
      foreach (string operation in FolderOwners.Keys.ToArray()) if (FolderOwners[operation] == app.SessionId) FolderOwners.Remove(operation);
    }
  }

  private static void PublishChanges()
  {
    if (!_ready) return;
    lock (PublisherGate) if (Apps.Count == 0) return;
    if (Interlocked.Exchange(ref _publishing, 1) != 0) return;
    ActivityTopic dirty = ActivityTopic.None;
    try
    {
      dirty = Changes.Drain();
      AppNotifications[] apps;
      lock (PublisherGate) apps = Apps.Values.ToArray();
      if ((dirty & ActivityTopic.Jobs) != 0)
      {
        Wire.Job[] jobs = TypedSnapshots.Jobs(LevelJobService.List());
        foreach (AppNotifications queue in apps) queue.Enqueue("jobs", () => queue.App.JobsChangedAsync(
          new Wire.JobsChangedRequest { Jobs = jobs }, queue.App.CancellationToken));
      }
      if ((dirty & ActivityTopic.Library) != 0)
      {
        Wire.LibrarySummary summary = SafeSummary();
        foreach (AppNotifications queue in apps) queue.Enqueue("library", () => queue.App.LibraryChangedAsync(
          new Wire.LibraryChangedRequest { Summary = summary }, queue.App.CancellationToken));
        KeyValuePair<string, string[]>[] interests;
        lock (PublisherGate) interests = LibraryInterests.ToArray();
        foreach (var interest in interests)
        {
          AppNotifications queue;
          lock (PublisherGate) if (!Apps.TryGetValue(interest.Key, out queue)) continue;
          string[] ids = DownloadedMembership(interest.Value);
          queue.Enqueue("membership", () => queue.App.MembershipChangedAsync(
            new Wire.MembershipChangedRequest { LevelIds = ids }, queue.App.CancellationToken));
        }
      }
      if ((dirty & ActivityTopic.Storage) != 0)
      {
        Wire.Storage storage = TypedSnapshots.Storage(DownloadStorageMigrationService.GetStatus());
        foreach (AppNotifications queue in apps) queue.Enqueue("storage", () => queue.App.StorageChangedAsync(
          new Wire.StorageChangedRequest { Storage = storage }, queue.App.CancellationToken));
      }
      if ((dirty & ActivityTopic.Batch) != 0)
      {
        Wire.Batch batch = TypedSnapshots.Batch(LevelUpdateCheckBatchService.GetStatus());
        foreach (AppNotifications queue in apps) queue.Enqueue("batch", () => queue.App.BatchChangedAsync(
          new Wire.BatchChangedRequest { Batch = batch }, queue.App.CancellationToken));
      }
      if ((dirty & ActivityTopic.Settings) != 0)
      {
        Wire.Settings settings = TypedSnapshots.Settings(DownloadSettingsService.GetStatus());
        foreach (AppNotifications queue in apps) queue.Enqueue("settings", () => queue.App.SettingsChangedAsync(
          new Wire.SettingsChangedRequest { Settings = settings }, queue.App.CancellationToken));
      }
      if ((dirty & ActivityTopic.Folder) != 0) PublishFolderSelections();
    }
    catch (Exception error) { Changes.Changed(dirty); Main.Instance?.LogException(error); }
    finally { Volatile.Write(ref _publishing, 0); }
  }

  private static void PublishFolderSelections()
  {
    KeyValuePair<string, string>[] owners;
    lock (PublisherGate) owners = FolderOwners.ToArray();
    foreach (var owner in owners)
    {
      DownloadFolderPickerSnapshot snapshot = DownloadFolderPickerCoordinator.GetStatus(owner.Key);
      if (snapshot.State == "picking") continue;
      AppNotifications queue;
      lock (PublisherGate)
      {
        FolderOwners.Remove(owner.Key);
        if (!Apps.TryGetValue(owner.Value, out queue)) continue;
        if (!string.IsNullOrEmpty(snapshot.SelectionToken))
        { SelectionOwners.Clear(); SelectionOwners[snapshot.SelectionToken] = owner.Value; }
      }
      Wire.FolderSelection selection = TypedSnapshots.Folder(snapshot);
      queue.Enqueue("folder", () => queue.App.FolderSelectionAsync(
        new Wire.FolderSelectionRequest { Selection = selection }, queue.App.CancellationToken));
    }
  }
}
