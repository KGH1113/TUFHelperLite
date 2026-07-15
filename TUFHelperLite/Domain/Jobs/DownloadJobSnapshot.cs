namespace TUFHelperLite.Domain.Jobs;

public sealed class DownloadJobSnapshot
{
  public string JobId { get; set; }
  public string Kind { get; set; }
  public string Status { get; set; }
  public string Stage { get; set; }
  public string Message { get; set; }
  public string CacheKey { get; set; }
  public int QueuePosition { get; set; }
  public double Progress { get; set; }
  public long BytesReceived { get; set; }
  public long TotalBytes { get; set; }
  public string LevelId { get; set; }
  public string Song { get; set; }
  public string Artist { get; set; }
  public string Creator { get; set; }
  public int DifficultyId { get; set; }
  public string SourceUrl { get; set; }
  public string DirectUrl { get; set; }
  public string Directory { get; set; }
  public string SelectedLevelPath { get; set; }
  public string[] LevelPaths { get; set; }
  public bool OpenAfterDownload { get; set; }
  public bool WaitingForSelection { get; set; }
  public bool Opened { get; set; }
  public bool FromCache { get; set; }
  public string Error { get; set; }
  public string ErrorCode { get; set; }
  public long ErrorAvailableBytes { get; set; }
  public long ErrorRequiredBytes { get; set; }
  public long CreatedAtUnixMs { get; set; }
  public long UpdatedAtUnixMs { get; set; }
  public bool Done { get; set; }
}
