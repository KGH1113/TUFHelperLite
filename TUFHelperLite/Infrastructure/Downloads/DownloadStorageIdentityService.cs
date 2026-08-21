using System;
using System.IO;
using Newtonsoft.Json;

namespace TUFHelperLite.Infrastructure.Downloads;

public sealed class DownloadStorageIdentity
{
  public int Version { get; set; }
  public string StorageId { get; set; }
  public string CreatedAtUtc { get; set; }
}

public static class DownloadStorageIdentityService
{
  public const string MarkerFileName = ".tufhelperlite-storage.json";
  private const int MarkerVersion = 1;

  public static DownloadStorageIdentity Initialize() => Ensure(DownloadCachePaths.GetDownloadRoot());

  public static DownloadStorageIdentity Ensure(string root, DownloadStorageIdentity preferred = null)
  {
    Directory.CreateDirectory(root);
    DownloadStorageIdentity existing = Read(root);
    if (existing != null) return existing;

    DownloadStorageIdentity marker = preferred ?? new DownloadStorageIdentity
    {
      Version = MarkerVersion,
      StorageId = Guid.NewGuid().ToString("N"),
      CreatedAtUtc = DateTime.UtcNow.ToString("O")
    };
    marker.Version = MarkerVersion;
    if (string.IsNullOrWhiteSpace(marker.StorageId)) marker.StorageId = Guid.NewGuid().ToString("N");
    if (string.IsNullOrWhiteSpace(marker.CreatedAtUtc)) marker.CreatedAtUtc = DateTime.UtcNow.ToString("O");
    Write(root, marker);
    return marker;
  }

  public static DownloadStorageIdentity Read(string root)
  {
    try
    {
      string path = Path.Combine(root, MarkerFileName);
      if (!File.Exists(path)) return null;
      DownloadStorageIdentity marker = JsonConvert.DeserializeObject<DownloadStorageIdentity>(File.ReadAllText(path));
      return marker?.Version == MarkerVersion && !string.IsNullOrWhiteSpace(marker.StorageId) ? marker : null;
    }
    catch (Exception exception)
    {
      Main.Instance?.Warning("Failed to read the download storage marker: " + exception.Message);
      return null;
    }
  }

  public static bool IsMarkerOrOsMetadata(string path)
  {
    string name = Path.GetFileName(path);
    return string.Equals(name, MarkerFileName, StringComparison.OrdinalIgnoreCase) ||
      string.Equals(name, MarkerFileName + ".tmp", StringComparison.OrdinalIgnoreCase) ||
      string.Equals(name, ".DS_Store", StringComparison.OrdinalIgnoreCase) ||
      string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase);
  }

  private static void Write(string root, DownloadStorageIdentity marker)
  {
    string path = Path.Combine(root, MarkerFileName);
    string temporary = path + ".tmp";
    File.WriteAllText(temporary, JsonConvert.SerializeObject(marker, Formatting.Indented));
    if (File.Exists(path)) File.Replace(temporary, path, null);
    else File.Move(temporary, path);
  }
}
