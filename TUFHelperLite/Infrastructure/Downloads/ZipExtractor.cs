using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace TUFHelperLite.Infrastructure.Downloads;

internal static class ZipExtractor
{
  // Adapted from JALib.Tools.Zipper (BSD-3-Clause).
  private static readonly Encoding ZipEncoding = Encoding.GetEncoding(949);
  private static readonly char[] InvalidFileNameCharacters = Path.GetInvalidFileNameChars();

  public static void Extract(byte[] archiveBytes, string destinationDirectory)
  {
    if (archiveBytes == null) throw new ArgumentNullException(nameof(archiveBytes));
    if (string.IsNullOrWhiteSpace(destinationDirectory))
    {
      throw new ArgumentException("Destination directory is required.", nameof(destinationDirectory));
    }

    string destinationRoot = Path.GetFullPath(destinationDirectory)
      .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    string destinationPrefix = destinationRoot + Path.DirectorySeparatorChar;
    StringComparison pathComparison = Path.DirectorySeparatorChar == '\\'
      ? StringComparison.OrdinalIgnoreCase
      : StringComparison.Ordinal;

    Directory.CreateDirectory(destinationRoot);

    using MemoryStream stream = new(archiveBytes, false);
    using ZipArchive archive = new(stream, ZipArchiveMode.Read, false, ZipEncoding);

    foreach (ZipArchiveEntry entry in archive.Entries)
    {
      string safeEntryName = SanitizeEntryName(entry.FullName);
      string outputPath = Path.GetFullPath(Path.Combine(destinationRoot, safeEntryName));
      bool isInsideDestination = outputPath.Equals(destinationRoot, pathComparison) ||
        outputPath.StartsWith(destinationPrefix, pathComparison);

      if (!isInsideDestination)
      {
        throw new InvalidDataException("Archive entry escapes the destination directory: " + entry.FullName);
      }

      if (string.IsNullOrEmpty(entry.Name))
      {
        Directory.CreateDirectory(outputPath);
        continue;
      }

      string parentDirectory = Path.GetDirectoryName(outputPath);
      if (!string.IsNullOrEmpty(parentDirectory)) Directory.CreateDirectory(parentDirectory);

      using Stream input = entry.Open();
      using FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
      input.CopyTo(output);
    }
  }

  private static string SanitizeEntryName(string entryName)
  {
    string[] parts = entryName.Replace('\\', '/').Split('/');

    for (int i = 0; i < parts.Length; i++)
    {
      char[] characters = parts[i].ToCharArray();
      bool hasReplacementCharacter = Array.IndexOf(characters, '\uFFFD') >= 0;

      for (int j = 0; j < characters.Length; j++)
      {
        char character = characters[j];
        if ((hasReplacementCharacter && character > 127) || char.IsControl(character) ||
            Array.IndexOf(InvalidFileNameCharacters, character) >= 0)
        {
          characters[j] = '_';
        }
      }

      parts[i] = new string(characters);
    }

    return string.Join(Path.DirectorySeparatorChar.ToString(), parts);
  }
}
