using System;
using TUFHelperLite.Domain.Errors;

namespace TUFHelperLite.Infrastructure.Downloads;

internal static class DiskSpacePolicy
{
  internal const long MinimumReserveBytes = 1L * 1024 * 1024 * 1024;
  internal const long MaximumReserveBytes = 5L * 1024 * 1024 * 1024;

  internal static long CalculateReserveBytes(long totalSize)
  {
    long percentageReserve = totalSize > 0 ? totalSize / 20 : 0;
    return Math.Max(MinimumReserveBytes, Math.Min(MaximumReserveBytes, percentageReserve));
  }

  internal static long CalculateRemainingBytes(long totalBytes, long completedBytes)
  {
    if (totalBytes <= 0) return 0;
    if (completedBytes <= 0) return totalBytes;
    return completedBytes >= totalBytes ? 0 : totalBytes - completedBytes;
  }

  internal static bool HasSufficientSpace(long availableBytes, long totalSize, long requiredBytes)
  {
    if (requiredBytes < 0) throw new ArgumentOutOfRangeException(nameof(requiredBytes));

    long reserve = CalculateReserveBytes(totalSize);
    return availableBytes >= reserve && requiredBytes <= availableBytes - reserve;
  }

  internal static void EnsureSufficientSpace(string path, long requiredBytes, string errorMessage)
  {
    if (!DiskSpaceProbe.TryGetSpace(path, out long availableBytes, out long totalBytes)) return;

    if (!HasSufficientSpace(availableBytes, totalBytes, requiredBytes))
    {
      long reserve = CalculateReserveBytes(totalBytes);
      long totalRequired = requiredBytes > long.MaxValue - reserve
        ? long.MaxValue
        : requiredBytes + reserve;
      throw new InsufficientDiskSpaceException(
        errorMessage,
        availableBytes,
        totalRequired);
    }
  }
}
