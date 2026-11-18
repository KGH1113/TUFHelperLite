using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;

namespace TUFHelperLite.Infrastructure.Downloads;

// Also compiled into TUFHelperLite.UpdateEngine.
internal static class DiskSpaceProbe
{
  // Returns false when free space cannot be measured, so callers can skip a check that has no data.
  internal static bool TryGetSpace(string path, out long availableBytes, out long totalBytes)
  {
    availableBytes = 0;
    totalBytes = 0;
    try
    {
      DriveInfo drive = FindDrive(Path.GetFullPath(path));
      if (drive == null) return false;

      availableBytes = drive.AvailableFreeSpace;
      totalBytes = drive.TotalSize;
      return totalBytes > 0;
    }
    catch (Exception exception) when (
      exception is ArgumentException or IOException or NotSupportedException or
        SecurityException or UnauthorizedAccessException)
    {
      return false;
    }
  }

  // Mono lists only device-backed mounts on Linux, so "/" is absent when the root is a tmpfs,
  // as it is inside the Steam Linux Runtime container.
  internal static string SelectMountPoint(string fullPath, IEnumerable<string> mountPoints)
  {
    string best = null;
    int bestLength = -1;
    foreach (string mountPoint in mountPoints)
    {
      if (string.IsNullOrEmpty(mountPoint)) continue;

      string prefix = mountPoint.TrimEnd('/');
      bool contains = prefix.Length == 0 ||
        fullPath == prefix ||
        fullPath.StartsWith(prefix + "/", StringComparison.Ordinal);
      if (contains && prefix.Length > bestLength)
      {
        best = mountPoint;
        bestLength = prefix.Length;
      }
    }
    return best;
  }

  private static DriveInfo FindDrive(string fullPath)
  {
    if (Path.DirectorySeparatorChar != '/')
    {
      string root = Path.GetPathRoot(fullPath);
      return string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root);
    }

    DriveInfo[] drives = DriveInfo.GetDrives();
    string mountPoint = SelectMountPoint(fullPath, drives.Select(drive => drive.Name));
    return mountPoint == null ? null : drives.First(drive => drive.Name == mountPoint);
  }
}
