namespace TUFHelperLite.Presentation.Ipc;

public sealed class HealthResponse
{
  public bool Ok;
  public string Mod;
  public string Version;
}

public sealed class OpenLevelByIdRequest
{
  public string Id;
  public string Source;
  public bool OpenAfterDownload = true;
}

public sealed class OpenLevelByUrlRequest
{
  public string Url;
  public string Source;
  public bool OpenAfterDownload = true;
}

public sealed class DownloadLevelRequest
{
  public string Url;
  public string LevelId;
  public string Source;
}

public sealed class JobStatusRequest
{
  public string JobId;
}

public sealed class SelectLevelRequest
{
  public string JobId;
  public string LevelPath;
}

public sealed class SelectLevelResponse
{
  public bool Ok;
  public string JobId;
  public string LevelPath;
  public bool Opened;
}

public sealed class JobListResponse
{
  public TUFHelperLite.Domain.Jobs.DownloadJobSnapshot[] Jobs;
}

public sealed class DownloadedLevelIdsResponse
{
  public string[] LevelIds;
}

public sealed class JobCancelResponse
{
  public bool Ok;
  public string JobId;
  public bool Cancelled;
}

public sealed class PendingResponse
{
  public bool Accepted;
  public string Method;
  public string Value;
  public string Status;
}

public sealed class LevelActionResponse
{
  public bool Ok;
  public string Status;
  public string LevelId;
  public string SourceUrl;
  public string DirectUrl;
  public string Directory;
  public string SelectedLevelPath;
  public string[] LevelPaths;
  public bool Opened;
}
