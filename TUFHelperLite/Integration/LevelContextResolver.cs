using System;
using System.IO;
using TUFHelperLite.Infrastructure.Downloads;

namespace TUFHelperLite.Integration;

public static class LevelContextResolver
{
  public static int? ResolveTufLevelId(string levelPath)
  {
    if (string.IsNullOrWhiteSpace(levelPath) ||
        !string.Equals(Path.GetExtension(levelPath), ".adofai", StringComparison.OrdinalIgnoreCase))
    {
      return null;
    }

    try
    {
      string canonicalLevelPath = Path.GetFullPath(levelPath);
      if (!File.Exists(canonicalLevelPath)) return null;

      string canonicalRoot = Path.GetFullPath(DownloadCachePaths.GetDownloadRoot());
      string relativePath = Path.GetRelativePath(canonicalRoot, canonicalLevelPath);
      if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath)) return null;

      string parentPrefix = ".." + Path.DirectorySeparatorChar;
      string alternateParentPrefix = ".." + Path.AltDirectorySeparatorChar;
      if (relativePath == ".." ||
          relativePath.StartsWith(parentPrefix, StringComparison.Ordinal) ||
          relativePath.StartsWith(alternateParentPrefix, StringComparison.Ordinal))
      {
        return null;
      }

      int separatorIndex = relativePath.IndexOfAny(new[]
      {
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
      });
      if (separatorIndex <= 0) return null;

      string cacheKey = relativePath.Substring(0, separatorIndex);
      return DownloadCachePaths.TryParseTufCacheKey(cacheKey, out int levelId) ? levelId : null;
    }
    catch (Exception exception) when (
      exception is ArgumentException ||
      exception is IOException ||
      exception is NotSupportedException ||
      exception is UnauthorizedAccessException)
    {
      return null;
    }
  }
}
