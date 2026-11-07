using System;
using System.Collections.Generic;
using System.IO;

namespace TUFHelperLite.Infrastructure.Downloads;

// Turns decoded zip entry names into relative paths the local file system accepts.
// Names stay unchanged whenever possible because .adofai files refer to their
// song and image files by name.
internal static class ZipEntryPaths
{
  // NTFS, APFS and HFS+ allow 255 UTF-16 units per name. ext4 counts 255 bytes
  // instead, which the shortened fallback name always fits.
  internal const int MaxComponentLength = 255;
  internal const int FallbackFileNameLength = 64;
  private const int MaxExtensionLength = 16;

  internal static readonly bool IsWindows = Path.DirectorySeparatorChar == '\\';

  private static readonly char[] WindowsInvalidCharacters = { '<', '>', ':', '"', '|', '?', '*' };

  internal static bool IsDirectoryEntry(string name) =>
    name.Length > 0 && (name[name.Length - 1] == '/' || name[name.Length - 1] == '\\');

  // Splits on '/' and '\', resolves "." and "..", and makes every segment a valid
  // file name. Returns an empty array when nothing is left.
  internal static string[] Split(string name, bool windows)
  {
    List<string> parts = new();
    foreach (string segment in name.Split('/', '\\'))
    {
      if (segment.Length == 0 || segment == ".") continue;

      if (segment == "..")
      {
        if (parts.Count == 0)
        {
          throw new InvalidDataException("Archive entry escapes the destination directory: " + name);
        }

        parts.RemoveAt(parts.Count - 1);
        continue;
      }

      parts.Add(Shorten(SanitizeComponent(segment, windows), MaxComponentLength, windows));
    }

    return parts.ToArray();
  }

  internal static string SanitizeComponent(string component, bool windows)
  {
    char[] characters = component.ToCharArray();
    for (int i = 0; i < characters.Length; i++)
    {
      char character = characters[i];
      bool pairedSurrogate = char.IsHighSurrogate(character)
        ? i + 1 < characters.Length && char.IsLowSurrogate(characters[i + 1])
        : char.IsLowSurrogate(character) && i > 0 && char.IsHighSurrogate(characters[i - 1]);

      if (char.IsControl(character) || character == '�' ||
          (char.IsSurrogate(character) && !pairedSurrogate) ||
          (windows && Array.IndexOf(WindowsInvalidCharacters, character) >= 0))
      {
        characters[i] = '_';
      }
    }

    string result = new(characters);
    if (windows)
    {
      // Windows drops trailing dots and spaces when it creates a folder but keeps
      // them in paths that go through it, so "Song ./level.adofai" fails.
      result = result.TrimEnd(' ', '.');
      if (IsReservedWindowsName(result)) result = "_" + result;
    }

    return result.Length == 0 ? "_" : result;
  }

  // Relative paths to try for a file, most faithful first. The later ones put the
  // file in a short hashed folder, then shorten its name too, for systems with
  // path limits (Windows MAX_PATH, 255-byte names on ext4, PATH_MAX on macOS).
  internal static List<string[]> FileCandidates(string[] components, bool windows)
  {
    List<string[]> candidates = new() { components };
    string fileName = components[components.Length - 1];
    string shortName = Shorten(fileName, FallbackFileNameLength, windows);

    if (components.Length > 1)
    {
      string folder = "~" + Hash(string.Join("/", components, 0, components.Length - 1));
      candidates.Add(new[] { folder, fileName });
      if (!string.Equals(shortName, fileName, StringComparison.Ordinal)) candidates.Add(new[] { folder, shortName });
    }
    else if (!string.Equals(shortName, fileName, StringComparison.Ordinal))
    {
      candidates.Add(new[] { shortName });
    }

    return candidates;
  }

  // Keeps the extension and adds a hash of the full name so shortened names stay unique.
  internal static string Shorten(string name, int maxLength, bool windows)
  {
    if (name.Length <= maxLength) return name;

    string suffix = "~" + Hash(name);
    int dot = name.LastIndexOf('.');
    string extension = dot > 0 && name.Length - dot <= MaxExtensionLength ? name.Substring(dot) : "";
    int keep = Math.Max(0, maxLength - suffix.Length - extension.Length);
    string stem = name.Substring(0, Math.Min(keep, name.Length - extension.Length));

    if (stem.Length > 0 && char.IsHighSurrogate(stem[stem.Length - 1])) stem = stem.Substring(0, stem.Length - 1);
    if (windows) stem = stem.TrimEnd(' ', '.');

    return stem + suffix + extension;
  }

  private static bool IsReservedWindowsName(string name)
  {
    int dot = name.IndexOf('.');
    string stem = (dot >= 0 ? name.Substring(0, dot) : name).TrimEnd(' ');

    if (stem.Length == 3)
    {
      return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
        stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
        stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
        stem.Equals("NUL", StringComparison.OrdinalIgnoreCase);
    }

    return stem.Length == 4 && stem[3] >= '1' && stem[3] <= '9' &&
      (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
       stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
  }

  // FNV-1a: stable across runs and runtimes, unlike string.GetHashCode.
  private static string Hash(string value)
  {
    uint hash = 2166136261;
    foreach (char character in value)
    {
      hash ^= character;
      hash *= 16777619;
    }

    return hash.ToString("x8");
  }
}
