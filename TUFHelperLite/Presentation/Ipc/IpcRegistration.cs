using System;
using System.Collections.Generic;
using System.Threading;
using TUFHelperLite.Domain.Ports;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using TUFHelperLite.App;
using TUFHelperLite.Domain.Jobs;
using TUFHelperLite.Infrastructure.Downloads;

namespace TUFHelperLite.Presentation.Ipc;

public static class IpcRegistration
{
  private static JsonFeature _namespace;
  private static readonly ActivityChangeBuffer Changes = new();
  private static readonly object PublisherGate = new();
  private static readonly Dictionary<string, string> FolderOwners = new();
  private static readonly Dictionary<string, string> SelectionOwners = new();
  private static readonly Dictionary<string, string[]> LibraryInterests = new();
  private static readonly HashSet<string> Subscribers = new();
  private static System.Threading.Timer _publisher;
  private static volatile bool _ready;
  private static int _publishing;

  private sealed class MainThreadDispatcher : TUFHelperLite.App.Ports.IMainThreadDispatcher
  {
    public void Dispatch(Action action) => IpcRuntime.Current.RunOnGameThread(action);
  }

  public static void Register()
  {
    _namespace = RegisterNamespace();
    ActivityChanges.Sink = Changes;
    DownloadFolderPickerCoordinator.MainThread = new MainThreadDispatcher();
    DownloadFolderPickerCoordinator.FolderPicker = new TUFHelperLite.Presentation.Unity.NativeFolderPicker();
    _namespace.PeerSubscribed += peer => { lock (PublisherGate) Subscribers.Add(peer.PeerId); if (_ready) SendSnapshot(peer.PeerId); };
    _namespace.PeerDisconnected += peer => { lock (PublisherGate) { Subscribers.Remove(peer.PeerId); LibraryInterests.Remove(peer.PeerId); foreach (string token in new List<string>(SelectionOwners.Keys)) if (SelectionOwners[token] == peer.PeerId) SelectionOwners.Remove(token); foreach (string id in new List<string>(FolderOwners.Keys)) if (FolderOwners[id] == peer.PeerId) FolderOwners.Remove(id); } };
    _publisher = new System.Threading.Timer(_ => PublishChanges(), null, 100, 100);
    ModStatus.SetNormal();
  }

  public static void MarkReady()
  {
    _ready = true;
    _namespace.MarkReady();
    _namespace.Publish("snapshot", Snapshot());
  }

  public static void MarkError(Exception exception)
  {
    _namespace?.MarkError(
      "tufhelperlite_initialization_failed",
      exception?.Message ?? "TUFHelperLite initialization failed.");
  }

  public static void Unregister()
  {
    try
    {
      _ready = false;
      ActivityChanges.Sink = null;
      _publisher?.Dispose();
      _publisher = null;
      lock (PublisherGate) { FolderOwners.Clear(); SelectionOwners.Clear(); LibraryInterests.Clear(); Subscribers.Clear(); }
      _namespace?.Dispose();
      _namespace = null;
    }
    catch (Exception e)
    {
      Main.Instance?.LogException(e);
    }
  }

  private static void RegisterHandlers(JsonFeature ipc)
  {
    ipc.RegisterCommand("snapshot.refresh", command => command.Reply("snapshot", Snapshot()));
    ipc.RegisterCommand("level.open-from-id", command => command.Reply("job.started", OpenFromId(command)));
    ipc.RegisterCommand("level.open-from-url", command => command.Reply("job.started", OpenFromUrl(command)));
    ipc.RegisterCommand("level.download", command => command.Reply("job.started", Download(command)));
    ipc.RegisterCommand("library.watch", command =>
    {
      string[] ids = command.Payload?["ids"]?.ToObject<string[]>() ?? Array.Empty<string>();
      if (ids.Length > 500 || ids.Any(id => !int.TryParse(id, out int number) || number <= 0)) { command.Reject("invalid_library_interest", "Choose at most 500 valid level IDs."); return; }
      ids = ids.Distinct().ToArray();
      lock (PublisherGate) LibraryInterests[command.PeerId] = ids;
      command.Reply("library.membership", new { LevelIds = DownloadedMembership(ids) });
    });
    ipc.RegisterCommand("library.page", command =>
    {
      try { command.Reply("library.page", DownloadedPage(command)); }
      catch (InvalidOperationException error) when (error.Message == "download_library_cursor_stale")
      { command.Reject("download_library_cursor_stale", "The download library changed. Reload it to continue."); }
    });
    ipc.RegisterCommand("level.update.check", command => command.Reply("job.started", UpdateCheck(command)));
    ipc.RegisterCommand("level.update.start", command => command.Reply("job.started", UpdateStart(command)));
    ipc.RegisterCommand("level.update.check-all.start", command => command.Reply("batch.changed", UpdateCheckAllStart(command)));
    ipc.RegisterCommand("level.update.check-all.cancel", command => command.Reply("batch.changed", UpdateCheckAllCancel(command)));
    ipc.RegisterCommand("level.update.all.start", command => command.Reply("batch.changed", UpdateAllStart(command)));
    ipc.RegisterCommand("level.update.all.cancel", command => command.Reply("batch.changed", UpdateAllCancel(command)));
    ipc.RegisterCommand("level.cancel", command => command.Reply("job.cancelled", Cancel(command)));
    ipc.RegisterCommand("level.select", command => command.Reply("level.selected", Select(command)));
    ipc.RegisterCommand("storage.folder-pick.start", command =>
    {
      DownloadFolderPickerSnapshot snapshot = (DownloadFolderPickerSnapshot)StorageFolderPickStart(command);
      if (snapshot.State == "picking") lock (PublisherGate) FolderOwners[snapshot.OperationId] = command.PeerId;
      command.Reply("folder.pick-started", snapshot);
      Changes.Changed(ActivityTopic.Folder);
    });
    ipc.RegisterCommand("storage.migration.start", command => command.Reply("storage.changed", StorageMigrationStart(command)));
    ipc.RegisterCommand("storage.migration.retry", command => command.Reply("storage.changed", StorageMigrationRetry(command)));
    ipc.RegisterCommand("storage.change.start", command => command.Reply("storage.changed", StorageChangeStart(command)));
    ipc.RegisterCommand("storage.change.retry", command => command.Reply("storage.changed", StorageChangeRetry(command)));
    ipc.RegisterCommand("storage.change.cancel", command => command.Reply("storage.changed", StorageChangeCancel(command)));

  }

  private static object Snapshot() => new
  {
    Health = Health(null),
    Jobs = LevelJobService.List(),
    LevelIds = Array.Empty<string>(),
    Storage = DownloadStorageMigrationService.GetStatus(),
    Batch = LevelUpdateCheckBatchService.GetStatus(),
    Summary = SafeSummary()
  };

  private static string[] DownloadedMembership(string[] ids)
  {
    string root = DownloadCachePaths.GetDownloadRoot();
    return ids.Where(id =>
    {
      string path = Path.Combine(root, DownloadCachePaths.BuildTufCacheKey(id));
      return Directory.Exists(path) && Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any(file => string.Equals(Path.GetExtension(file), ".adofai", StringComparison.OrdinalIgnoreCase));
    }).ToArray();
  }

  private static object SafeSummary()
  {
    try { return DownloadLibraryService.GetSummary(); }
    catch { return null; } // Library reads can be temporarily blocked during storage cutover.
  }

  private static void SendSnapshot(string peerId)
  {
    try { _namespace?.SendToPeer(peerId, "snapshot", Snapshot()); }
    catch (Exception exception) { Main.Instance?.LogException(exception); }
  }

  private static void PublishChanges()
  {
    if (!_ready) return;
    lock (PublisherGate) if (Subscribers.Count == 0) return;
    if (Interlocked.Exchange(ref _publishing, 1) != 0) return;
    ActivityTopic dirty = ActivityTopic.None;
    try
    {
      dirty = Changes.Drain();
      JsonFeature ipc = _namespace;
      if (ipc == null) return;
      if ((dirty & ActivityTopic.Jobs) != 0) ipc.Publish("jobs.changed", new { Jobs = LevelJobService.List() });
      if ((dirty & ActivityTopic.Library) != 0)
      {
        ipc.Publish("library.changed", new { Summary = SafeSummary() });
        KeyValuePair<string, string[]>[] interests;
        lock (PublisherGate) interests = new List<KeyValuePair<string, string[]>>(LibraryInterests).ToArray();
        foreach (KeyValuePair<string, string[]> interest in interests) ipc.SendToPeer(interest.Key, "library.membership", new { LevelIds = DownloadedMembership(interest.Value) });
      }
      if ((dirty & ActivityTopic.Storage) != 0) ipc.Publish("storage.changed", DownloadStorageMigrationService.GetStatus());
      if ((dirty & ActivityTopic.Batch) != 0) ipc.Publish("batch.changed", LevelUpdateCheckBatchService.GetStatus());
      if ((dirty & ActivityTopic.Folder) != 0)
      {
        KeyValuePair<string, string>[] owners;
        lock (PublisherGate) owners = new List<KeyValuePair<string, string>>(FolderOwners).ToArray();
        foreach (KeyValuePair<string, string> owner in owners)
        {
          DownloadFolderPickerSnapshot snapshot = DownloadFolderPickerCoordinator.GetStatus(owner.Key);
          if (snapshot.State == "picking") continue;
          if (!string.IsNullOrEmpty(snapshot.SelectionToken)) lock (PublisherGate) { SelectionOwners.Clear(); SelectionOwners[snapshot.SelectionToken] = owner.Value; }
          ipc.SendToPeer(owner.Value, "folder.selection", snapshot);
          lock (PublisherGate) FolderOwners.Remove(owner.Key);
        }
      }
    }
    catch (Exception exception) { Changes.Changed(dirty); Main.Instance?.LogException(exception); }
    finally { Volatile.Write(ref _publishing, 0); }
  }

  private static JsonFeature RegisterNamespace()
  {
    return JsonFeature.Register(
      new AdofaiIpc.Contracts.FeatureDescription("tufhelperlite", ModStatus.Version,
        protocolMajor: 1, displayName: ModStatus.DisplayName, allowedOrigins: new[]
        {
          "https://tuforums.com", "http://localhost", "http://127.0.0.1",
          "https://guhyeons-macbook-pro.tail234c02.ts.net"
        }), RegisterHandlers);
  }

  private static object Health(JsonCommand request)
  {
    return new HealthResponse
    {
      Ok = true,
      Mod = "TUFHelperLite",
      Version = Main.Instance.Version.ToString(),
      Capabilities = new[]
      {
        "download-storage-migration-v1",
        "downloaded-level-library-v1",
        "downloaded-level-positioned-pages-v1",
        "downloaded-level-update-v1",
        "downloaded-level-batch-update-check-v1",
        "downloaded-level-batch-update-v1",
        "download-storage-reconnect-v1"
      }
    };
  }

  private static object OpenFromId(JsonCommand request)
  {
    OpenLevelByIdRequest body = ReadPayload<OpenLevelByIdRequest>(request);

    return LevelJobService.StartOpenFromId(body?.Id, body == null || body.OpenAfterDownload);
  }

  private static object OpenFromUrl(JsonCommand request)
  {
    OpenLevelByUrlRequest body = ReadPayload<OpenLevelByUrlRequest>(request);

    return LevelJobService.StartOpenFromUrl(body?.Url, body == null || body.OpenAfterDownload);
  }

  private static object Download(JsonCommand request)
  {
    DownloadLevelRequest body = ReadPayload<DownloadLevelRequest>(request);

    return LevelJobService.StartDownload(body?.Url, body?.LevelId);
  }

  private static object DownloadedPage(JsonCommand request)
  {
    DownloadedLevelPageRequest body = ReadPayload<DownloadedLevelPageRequest>(request);
    return DownloadLibraryService.GetPage(body?.Cursor, body?.Direction, body?.Limit ?? 0);
  }

  private static object UpdateCheck(JsonCommand request)
  {
    LevelUpdateRequest body = ReadPayload<LevelUpdateRequest>(request);
    return LevelJobService.StartUpdateCheck(body?.Id);
  }

  private static object UpdateStart(JsonCommand request)
  {
    LevelUpdateRequest body = ReadPayload<LevelUpdateRequest>(request);
    return LevelJobService.StartUpdate(body?.Id);
  }

  private static object UpdateCheckAllStart(JsonCommand request) => LevelUpdateCheckBatchService.Start();

  private static object UpdateCheckAllCancel(JsonCommand request) => LevelUpdateCheckBatchService.Cancel();

  private static object UpdateAllStart(JsonCommand request) => LevelUpdateCheckBatchService.StartUpdateAll();

  private static object UpdateAllCancel(JsonCommand request) => LevelUpdateCheckBatchService.Cancel();

  private static object Cancel(JsonCommand request)
  {
    JobStatusRequest body = ReadPayload<JobStatusRequest>(request);
    bool cancelled = LevelJobService.Cancel(body?.JobId);

    return new JobCancelResponse
    {
      Ok = cancelled,
      JobId = body?.JobId,
      Cancelled = cancelled
    };
  }

  private static object Select(JsonCommand request)
  {
    SelectLevelRequest body = ReadPayload<SelectLevelRequest>(request);
    bool opened = LevelJobService.SelectLevel(body?.JobId, body?.LevelPath);

    return new SelectLevelResponse
    {
      Ok = opened,
      JobId = body?.JobId,
      LevelPath = body?.LevelPath,
      Opened = opened
    };
  }

  private static object StorageFolderPickStart(JsonCommand request)
  {
    FolderPickerStartRequest body = ReadPayload<FolderPickerStartRequest>(request);
    return DownloadFolderPickerCoordinator.Start(body?.AllowExisting == true);
  }

  private static object StorageMigrationStart(JsonCommand request)
  {
    StorageMigrationStartRequest body = ReadPayload<StorageMigrationStartRequest>(request);
    if (body?.UseDefault != true)
    {
      lock (PublisherGate)
      {
        if (body == null || string.IsNullOrEmpty(body.SelectionToken) || !SelectionOwners.TryGetValue(body.SelectionToken, out string peerId) || peerId != request.PeerId)
        { request.Reject("selection_token_invalid", "Choose a download folder again to continue."); return null; }
      }
    }
    return DownloadStorageMigrationService.Start(body?.SelectionToken, body?.UseDefault == true);
  }

  private static object StorageMigrationRetry(JsonCommand request)
  {
    return DownloadStorageMigrationService.Retry();
  }

  private static object StorageChangeStart(JsonCommand request)
  {
    StorageMigrationStartRequest body = ReadPayload<StorageMigrationStartRequest>(request);
    if (body?.UseDefault != true)
    {
      lock (PublisherGate)
      {
        if (body == null || string.IsNullOrEmpty(body.SelectionToken) || !SelectionOwners.TryGetValue(body.SelectionToken, out string peerId) || peerId != request.PeerId)
        { request.Reject("selection_token_invalid", "Choose a download folder again to continue."); return null; }
      }
    }
    return DownloadStorageMigrationService.StartChange(body?.SelectionToken, body?.UseDefault == true);
  }

  private static object StorageChangeRetry(JsonCommand request) => DownloadStorageMigrationService.Retry();

  private static object StorageChangeCancel(JsonCommand request) => DownloadStorageMigrationService.CancelChange();

  private static T ReadPayload<T>(JsonCommand request) where T : class
  {
    if (request?.Payload == null || request.Payload.Type == JTokenType.Null)
    {
      return null;
    }

    return request.Payload.ToObject<T>();
  }

}
