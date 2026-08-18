namespace TUFHelperLite.Domain.Downloads;

public sealed class DownloadedLevelItem
{
  public int Id { get; set; }
  public int DiffId { get; set; }
  public string Artist { get; set; }
  public string LevelName { get; set; }
  public string Creator { get; set; }
  public long SizeBytes { get; set; }
  public long DownloadedAtUnixMs { get; set; }
  public string DownloadedAtUtc { get; set; }
  public string MetadataState { get; set; }
}

public sealed class DownloadedLevelPage
{
  public long Revision { get; set; }
  public DownloadedLevelItem[] Items { get; set; }
  public string NextCursor { get; set; }
  public string PreviousCursor { get; set; }
  public bool HasNext { get; set; }
  public bool HasPrevious { get; set; }
}

public sealed class DownloadLibrarySummary
{
  public string State { get; set; }
  public long Revision { get; set; }
  public int LevelCount { get; set; }
  public long TotalSizeBytes { get; set; }
  public string LastCalculatedAtUtc { get; set; }
  public string ErrorCode { get; set; }
  public string Message { get; set; }
}
