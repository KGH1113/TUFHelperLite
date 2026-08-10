using System;
using System.IO;

namespace TUFHelperLite.UpdateEngine;

internal static class UpdateDiskSpacePolicy
{
  private const long MinimumReserveBytes = 1L * 1024 * 1024 * 1024;
  private const long MaximumReserveBytes = 5L * 1024 * 1024 * 1024;

  public static long CalculateRemainingBytes(long totalBytes, long completedBytes)
  {
    if (totalBytes <= 0) return 0;
    if (completedBytes <= 0) return totalBytes;
    return completedBytes >= totalBytes ? 0 : totalBytes - completedBytes;
  }

  public static void EnsureSufficientSpace(string path, long requiredBytes, string errorMessage)
  {
    if (requiredBytes < 0) throw new ArgumentOutOfRangeException(nameof(requiredBytes));

    DriveInfo drive = GetDrive(path);
    long reserve = CalculateReserveBytes(drive.TotalSize);
    if (drive.AvailableFreeSpace < reserve || requiredBytes > drive.AvailableFreeSpace - reserve)
      throw new IOException(errorMessage);
  }

  private static long CalculateReserveBytes(long totalSize)
  {
    long percentageReserve = totalSize > 0 ? totalSize / 20 : 0;
    return Math.Max(MinimumReserveBytes, Math.Min(MaximumReserveBytes, percentageReserve));
  }

  private static DriveInfo GetDrive(string path)
  {
    string root = Path.GetPathRoot(Path.GetFullPath(path));
    if (string.IsNullOrWhiteSpace(root))
      throw new IOException("Could not determine the destination drive.");
    return new DriveInfo(root);
  }
}
