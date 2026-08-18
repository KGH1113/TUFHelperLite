namespace TUFHelperLite.Domain.Storage;

public sealed class DownloadStorageMigrationSnapshot
{
  public string OperationId { get; set; }
  public string State { get; set; } = "idle";
  public string SourceDirectory { get; set; }
  public string TargetDirectory { get; set; }
  public string CurrentDirectory { get; set; }
  public int FilesProcessed { get; set; }
  public int FilesTotal { get; set; }
  public long BytesProcessed { get; set; }
  public long BytesTotal { get; set; }
  public string ErrorCode { get; set; }
  public string Message { get; set; }
  public bool IsDefault { get; set; }
  public string DefaultDirectory { get; set; }
}
