using TUFHelperLite.Domain.Downloads;

namespace TUFHelperLite.App;

internal sealed class LevelOpenUpdateCheckResult
{
  public const string NotInstalled = "not_installed";
  public const string UpToDate = "up_to_date";
  public const string UpdateAvailable = "update_available";

  public string State { get; set; }
  public DownloadedLevelItem Item { get; set; }
  public string InstalledFileId { get; set; }
  public string AvailableFileId { get; set; }
  public string AvailableUpdatedAtUtc { get; set; }
}
