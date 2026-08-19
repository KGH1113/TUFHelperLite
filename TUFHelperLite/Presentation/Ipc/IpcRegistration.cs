using System;
using AdofaiIpc.Core;
using Newtonsoft.Json.Linq;
using TUFHelperLite.App;
using TUFHelperLite.Domain.Jobs;
using TUFHelperLite.Infrastructure.Downloads;

namespace TUFHelperLite.Presentation.Ipc;

public static class IpcRegistration
{
  private static global::AdofaiIpc.AdofaiIpcNamespace _namespace;

  public static void Register()
  {
    _namespace = RegisterNamespace();
    RegisterHandlers(_namespace);
    ModStatus.SetNormal();
  }

  public static void MarkReady()
  {
    _namespace.MarkReady();
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
      global::AdofaiIpc.AdofaiIpc.UnregisterNamespace("tufhelperlite");
      _namespace = null;
    }
    catch (Exception e)
    {
      Main.Instance?.LogException(e);
    }
  }

  private static void RegisterHandlers(global::AdofaiIpc.AdofaiIpcNamespace ipc)
  {
    ipc.Register("health", Health);
    ipc.Register("level.open-from-id", OpenFromId);
    ipc.Register("level.open-from-url", OpenFromUrl);
    ipc.Register("level.download", Download);
    ipc.Register("level.status", Status);
    ipc.Register("level.jobs", Jobs);
    ipc.Register("level.downloaded-ids", DownloadedIds);
    ipc.Register("level.downloaded-page", DownloadedPage);
    ipc.Register("level.downloaded-summary", DownloadedSummary);
    ipc.Register("level.update.check", UpdateCheck);
    ipc.Register("level.update.start", UpdateStart);
    ipc.Register("level.cancel", Cancel);
    ipc.Register("level.select", Select);
    ipc.Register("storage.get", StorageGet);
    ipc.Register("storage.folder-pick.start", StorageFolderPickStart);
    ipc.Register("storage.folder-pick.status", StorageFolderPickStatus);
    ipc.Register("storage.migration.start", StorageMigrationStart);
    ipc.Register("storage.migration.status", StorageMigrationStatus);
    ipc.Register("storage.migration.retry", StorageMigrationRetry);
  }

  private static global::AdofaiIpc.AdofaiIpcNamespace RegisterNamespace()
  {
    return global::AdofaiIpc.AdofaiIpc.RegisterNamespace(
      "tufhelperlite",
      new global::AdofaiIpc.IpcNamespaceInfo
      {
        DisplayName = ModStatus.DisplayName,
        Version = ModStatus.Version,
        AllowedOrigins = new[]
        {
          "https://tuforums.com",
          "http://localhost",
          "http://127.0.0.1",
          "https://guhyeons-macbook-pro.tail234c02.ts.net"
        }
      });
  }

  private static object Health(IpcRequest request)
  {
    return new HealthResponse
    {
      Ok = true,
      Mod = "TUFHelperLite",
      Version = Main.Instance.Version.ToString(),
      Capabilities = new[] { "download-storage-migration-v1", "downloaded-level-library-v1", "downloaded-level-update-v1" }
    };
  }

  private static object OpenFromId(IpcRequest request)
  {
    OpenLevelByIdRequest body = ReadParams<OpenLevelByIdRequest>(request);

    return LevelJobService.StartOpenFromId(body?.Id, body == null || body.OpenAfterDownload);
  }

  private static object OpenFromUrl(IpcRequest request)
  {
    OpenLevelByUrlRequest body = ReadParams<OpenLevelByUrlRequest>(request);

    return LevelJobService.StartOpenFromUrl(body?.Url, body == null || body.OpenAfterDownload);
  }

  private static object Download(IpcRequest request)
  {
    DownloadLevelRequest body = ReadParams<DownloadLevelRequest>(request);

    return LevelJobService.StartDownload(body?.Url, body?.LevelId);
  }

  private static object Status(IpcRequest request)
  {
    JobStatusRequest body = ReadParams<JobStatusRequest>(request);
    DownloadJobSnapshot snapshot = LevelJobService.Get(body?.JobId);

    if (snapshot == null)
    {
      throw new InvalidOperationException($"Job not found: {body?.JobId}");
    }

    return snapshot;
  }

  private static object Jobs(IpcRequest request)
  {
    return new JobListResponse
    {
      Jobs = LevelJobService.List()
    };
  }

  private static object DownloadedIds(IpcRequest request)
  {
    return new DownloadedLevelIdsResponse
    {
      LevelIds = LevelArchiveDownloader.GetDownloadedLevelIds()
    };
  }

  private static object DownloadedPage(IpcRequest request)
  {
    DownloadedLevelPageRequest body = ReadParams<DownloadedLevelPageRequest>(request);
    return DownloadLibraryService.GetPage(body?.Cursor, body?.Direction, body?.Limit ?? 0);
  }

  private static object DownloadedSummary(IpcRequest request)
  {
    return DownloadLibraryService.GetSummary();
  }

  private static object UpdateCheck(IpcRequest request)
  {
    LevelUpdateRequest body = ReadParams<LevelUpdateRequest>(request);
    return LevelJobService.StartUpdateCheck(body?.Id);
  }

  private static object UpdateStart(IpcRequest request)
  {
    LevelUpdateRequest body = ReadParams<LevelUpdateRequest>(request);
    return LevelJobService.StartUpdate(body?.Id);
  }

  private static object Cancel(IpcRequest request)
  {
    JobStatusRequest body = ReadParams<JobStatusRequest>(request);
    bool cancelled = LevelJobService.Cancel(body?.JobId);

    return new JobCancelResponse
    {
      Ok = cancelled,
      JobId = body?.JobId,
      Cancelled = cancelled
    };
  }

  private static object Select(IpcRequest request)
  {
    SelectLevelRequest body = ReadParams<SelectLevelRequest>(request);
    bool opened = LevelJobService.SelectLevel(body?.JobId, body?.LevelPath);

    return new SelectLevelResponse
    {
      Ok = opened,
      JobId = body?.JobId,
      LevelPath = body?.LevelPath,
      Opened = opened
    };
  }

  private static object StorageGet(IpcRequest request)
  {
    return DownloadStorageMigrationService.GetStatus();
  }

  private static object StorageFolderPickStart(IpcRequest request)
  {
    return DownloadFolderPickerCoordinator.Start();
  }

  private static object StorageFolderPickStatus(IpcRequest request)
  {
    FolderPickerStatusRequest body = ReadParams<FolderPickerStatusRequest>(request);
    return DownloadFolderPickerCoordinator.GetStatus(body?.OperationId);
  }

  private static object StorageMigrationStart(IpcRequest request)
  {
    StorageMigrationStartRequest body = ReadParams<StorageMigrationStartRequest>(request);
    return DownloadStorageMigrationService.Start(body?.SelectionToken, body?.UseDefault == true);
  }

  private static object StorageMigrationStatus(IpcRequest request)
  {
    return DownloadStorageMigrationService.GetStatus();
  }

  private static object StorageMigrationRetry(IpcRequest request)
  {
    return DownloadStorageMigrationService.Retry();
  }

  private static T ReadParams<T>(IpcRequest request) where T : class
  {
    if (request?.Params == null || request.Params.Type == JTokenType.Null)
    {
      return null;
    }

    return request.Params.ToObject<T>();
  }

}
