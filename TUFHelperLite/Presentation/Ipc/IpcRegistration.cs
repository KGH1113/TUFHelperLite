using System;
using AdofaiIpc.Core;
using Newtonsoft.Json.Linq;
using TUFHelperLite.App;
using TUFHelperLite.Domain.Jobs;
using TUFHelperLite.Infrastructure.Downloads;

namespace TUFHelperLite.Presentation.Ipc;

public static class IpcRegistration
{
  public static void Register()
  {
    try
    {
      RegisterHandlers();
      ModStatus.SetNormal();
    }
    catch (Exception e)
    {
      ModStatus.SetAdofaiIpcState(ModStatus.GetAdofaiIpcState());
      Main.Instance.Warning("TUFHelperLite needs AdofaiIpc. IPC commands were not registered.");
      Main.Instance.LogException(e);
    }
  }

  public static void Unregister()
  {
    try
    {
      global::AdofaiIpc.AdofaiIpc.UnregisterNamespace("tufhelperlite");
    }
    catch (Exception e)
    {
      Main.Instance?.LogException(e);
    }
  }

  private static void RegisterHandlers()
  {
    global::AdofaiIpc.AdofaiIpcNamespace ipc =
      global::AdofaiIpc.AdofaiIpc.RegisterNamespace("tufhelperlite", new global::AdofaiIpc.IpcNamespaceInfo
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

    ipc.Register("health", Health);
    ipc.Register("level.open-from-id", OpenFromId);
    ipc.Register("level.open-from-url", OpenFromUrl);
    ipc.Register("level.download", Download);
    ipc.Register("level.status", Status);
    ipc.Register("level.jobs", Jobs);
    ipc.Register("level.downloaded-ids", DownloadedIds);
    ipc.Register("level.cancel", Cancel);
    ipc.Register("level.select", Select);
  }

  private static object Health(IpcRequest request)
  {
    return new HealthResponse
    {
      Ok = true,
      Mod = "TUFHelperLite",
      Version = Main.Instance.Version.ToString()
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

  private static T ReadParams<T>(IpcRequest request) where T : class
  {
    if (request?.Params == null || request.Params.Type == JTokenType.Null)
    {
      return null;
    }

    return request.Params.ToObject<T>();
  }

}
