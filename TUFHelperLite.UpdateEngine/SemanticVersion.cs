using System;
using System.Globalization;
using System.Linq;

namespace TUFHelperLite.UpdateEngine;

internal sealed class SemanticVersion : IComparable<SemanticVersion>
{
  private readonly int[] _core;
  private readonly string[] _prerelease;

  private SemanticVersion(int[] core, string[] prerelease)
  {
    _core = core;
    _prerelease = prerelease;
  }

  public static SemanticVersion Parse(string value)
  {
    if (!TryParse(value, out SemanticVersion version))
      throw new FormatException("Invalid semantic version: " + value);
    return version;
  }

  public static bool TryParse(string value, out SemanticVersion version)
  {
    version = null;
    if (string.IsNullOrWhiteSpace(value))
      return false;
    string normalized = value.Trim().TrimStart('v', 'V');
    int buildSeparator = normalized.IndexOf('+');
    if (buildSeparator >= 0)
      normalized = normalized.Substring(0, buildSeparator);
    string prereleaseText = null;
    int prereleaseSeparator = normalized.IndexOf('-');
    if (prereleaseSeparator >= 0)
    {
      prereleaseText = normalized.Substring(prereleaseSeparator + 1);
      normalized = normalized.Substring(0, prereleaseSeparator);
    }
    string[] coreParts = normalized.Split('.');
    if (coreParts.Length is < 1 or > 4)
      return false;
    int[] core = new int[Math.Max(3, coreParts.Length)];
    for (int index = 0; index < coreParts.Length; index++)
    {
      if (!int.TryParse(coreParts[index], NumberStyles.None, CultureInfo.InvariantCulture, out core[index]) || core[index] < 0)
        return false;
    }
    string[] prerelease = Array.Empty<string>();
    if (prereleaseText != null)
    {
      prerelease = prereleaseText.Split('.');
      if (prerelease.Length == 0 || prerelease.Any(identifier => !IsValidIdentifier(identifier)))
        return false;
    }
    version = new SemanticVersion(core, prerelease);
    return true;
  }

  public int CompareTo(SemanticVersion other)
  {
    if (other == null)
      return 1;
    int coreLength = Math.Max(_core.Length, other._core.Length);
    for (int index = 0; index < coreLength; index++)
    {
      int comparison = (index < _core.Length ? _core[index] : 0)
        .CompareTo(index < other._core.Length ? other._core[index] : 0);
      if (comparison != 0)
        return comparison;
    }
    if (_prerelease.Length == 0 || other._prerelease.Length == 0)
      return _prerelease.Length == other._prerelease.Length ? 0 : (_prerelease.Length == 0 ? 1 : -1);
    int length = Math.Min(_prerelease.Length, other._prerelease.Length);
    for (int index = 0; index < length; index++)
    {
      int comparison = CompareIdentifier(_prerelease[index], other._prerelease[index]);
      if (comparison != 0)
        return comparison;
    }
    return _prerelease.Length.CompareTo(other._prerelease.Length);
  }

  public override string ToString()
  {
    string core = string.Join(".", _core.Select(value => value.ToString(CultureInfo.InvariantCulture)));
    return _prerelease.Length == 0 ? core : core + "-" + string.Join(".", _prerelease);
  }

  private static int CompareIdentifier(string left, string right)
  {
    bool leftNumeric = left.All(char.IsDigit);
    bool rightNumeric = right.All(char.IsDigit);
    if (leftNumeric && rightNumeric)
    {
      string normalizedLeft = left.TrimStart('0');
      string normalizedRight = right.TrimStart('0');
      int comparison = normalizedLeft.Length.CompareTo(normalizedRight.Length);
      return comparison != 0 ? comparison : string.CompareOrdinal(normalizedLeft, normalizedRight);
    }
    if (leftNumeric != rightNumeric)
      return leftNumeric ? -1 : 1;
    return string.CompareOrdinal(left, right);
  }

  private static bool IsValidIdentifier(string value)
  {
    return !string.IsNullOrEmpty(value) && value.All(character => char.IsLetterOrDigit(character) || character == '-');
  }
}
