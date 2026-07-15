using System;

namespace TUFHelperLite.Bootstrap;

internal static class UpdateVersion
{
  public static bool TryParse(string value, out Version version)
  {
    version = null;
    if (string.IsNullOrWhiteSpace(value)) return false;
    string normalized = value.Trim();
    if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
      normalized = normalized.Substring(1);
    return Version.TryParse(normalized, out version);
  }

  public static bool IsNewer(string candidate, string current)
  {
    return TryParse(candidate, out Version candidateVersion) &&
           TryParse(current, out Version currentVersion) &&
           candidateVersion > currentVersion;
  }

  public static string Normalize(string value)
  {
    if (!TryParse(value, out Version version))
      throw new FormatException("Update version is invalid: " + value);
    return version.ToString();
  }
}
