using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TUFHelperLite.Infrastructure.Downloads;

internal static class DownloadCachePaths
{
  private const string TufCachePrefix = "tuf-";

  public static string GetDownloadRoot()
  {
    string modPath = Main.Instance?.ModEntry?.Path;
    if (string.IsNullOrWhiteSpace(modPath))
    {
      modPath = AppDomain.CurrentDomain.BaseDirectory;
    }

    return Path.Combine(modPath, "Downloads");
  }

  public static string BuildTufCacheKey(string levelId)
  {
    return TufCachePrefix + NormalizeLevelId(levelId);
  }

  public static string NormalizeLevelId(string levelId)
  {
    return (levelId ?? "").Trim().TrimStart('#');
  }

  public static bool TryParseTufCacheKey(string cacheKey, out int levelId)
  {
    levelId = 0;
    if (string.IsNullOrEmpty(cacheKey) ||
        !cacheKey.StartsWith(TufCachePrefix, StringComparison.OrdinalIgnoreCase))
    {
      return false;
    }

    string id = cacheKey.Substring(TufCachePrefix.Length);
    return id.Length > 0 &&
      id.All(character => character >= '0' && character <= '9') &&
      int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out levelId) &&
      levelId > 0;
  }
}
