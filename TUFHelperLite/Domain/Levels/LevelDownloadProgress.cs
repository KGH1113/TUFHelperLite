namespace TUFHelperLite.Domain.Levels;

public sealed class LevelDownloadProgress
{
  public string Stage { get; set; }
  public double Progress { get; set; } = -1;
  public long BytesReceived { get; set; } = -1;
  public long TotalBytes { get; set; } = -1;
  public string Message { get; set; }
}
