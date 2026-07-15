using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace TUFHelperLite.Infrastructure.Downloads;

internal static class ZipExtractor
{
  private const int MaxEntryCount = 50000;
  private const int MaxPathDepth = 32;
  private const long ExpansionAllowanceBytes = 1L * 1024 * 1024 * 1024;
  private const long DiskCheckIntervalBytes = 64L * 1024 * 1024;
  private const int MaxExpansionRatio = 200;
  private const int CopyBufferSize = 128 * 1024;

  // Adapted from JALib.Tools.Zipper (BSD-3-Clause).
  private static readonly Encoding ZipEncoding = Encoding.GetEncoding(949);
  private static readonly char[] InvalidFileNameCharacters = Path.GetInvalidFileNameChars();

  public static void Extract(string archivePath, string destinationDirectory, CancellationToken cancellationToken)
  {
    if (string.IsNullOrWhiteSpace(archivePath))
    {
      throw new ArgumentException("Archive path is required.", nameof(archivePath));
    }

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

    using FileStream stream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    using ZipArchive archive = new(stream, ZipArchiveMode.Read, false, ZipEncoding);
    ExtractionPlan plan = ValidateArchive(archive, stream.Length, destinationRoot);
    byte[] buffer = new byte[CopyBufferSize];
    long totalWritten = 0;
    long nextDiskCheck = DiskCheckIntervalBytes;

    foreach (ZipArchiveEntry entry in archive.Entries)
    {
      cancellationToken.ThrowIfCancellationRequested();

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
      int bytesRead;
      while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
      {
        cancellationToken.ThrowIfCancellationRequested();
        output.Write(buffer, 0, bytesRead);
        totalWritten = checked(totalWritten + bytesRead);

        if (totalWritten > plan.ExpansionLimit)
        {
          throw new InvalidDataException("Archive expands beyond the adaptive safety limit.");
        }

        if (totalWritten >= nextDiskCheck)
        {
          long remainingBytes = DiskSpacePolicy.CalculateRemainingBytes(plan.TotalLength, totalWritten);
          EnsureAvailableSpace(destinationRoot, remainingBytes);
          nextDiskCheck = checked(totalWritten + DiskCheckIntervalBytes);
        }
      }
    }
  }

  internal static void EnsureDiskReserve(string path)
  {
    EnsureAvailableSpace(path, 0);
  }

  internal static void EnsureAvailableSpace(string path, long requiredBytes)
  {
    DiskSpacePolicy.EnsureSufficientSpace(
      path,
      requiredBytes,
      "Not enough free disk space to safely continue the archive operation.");
  }

  private static ExtractionPlan ValidateArchive(ZipArchive archive, long archiveLength, string destinationRoot)
  {
    if (archive.Entries.Count > MaxEntryCount)
    {
      throw new InvalidDataException($"Archive contains too many entries ({archive.Entries.Count}).");
    }

    long totalLength = 0;
    foreach (ZipArchiveEntry entry in archive.Entries)
    {
      int depth = entry.FullName.Replace('\\', '/')
        .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
        .Length;
      if (depth > MaxPathDepth)
      {
        throw new InvalidDataException($"Archive entry path is too deep: {entry.FullName}");
      }

      totalLength = checked(totalLength + entry.Length);
    }

    long expansionLimit = ExpansionLimit(archiveLength);
    if (totalLength > expansionLimit)
    {
      throw new InvalidDataException("Archive declares an unsafe compression expansion ratio.");
    }

    DiskSpacePolicy.EnsureSufficientSpace(
      destinationRoot,
      totalLength,
      "Not enough free disk space to extract this archive safely.");

    return new ExtractionPlan(expansionLimit, totalLength);
  }

  private static long ExpansionLimit(long archiveLength)
  {
    if (archiveLength > (long.MaxValue - ExpansionAllowanceBytes) / MaxExpansionRatio)
    {
      return long.MaxValue;
    }

    return archiveLength * MaxExpansionRatio + ExpansionAllowanceBytes;
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

  private sealed class ExtractionPlan
  {
    public ExtractionPlan(long expansionLimit, long totalLength)
    {
      ExpansionLimit = expansionLimit;
      TotalLength = totalLength;
    }

    public long ExpansionLimit { get; }
    public long TotalLength { get; }
  }
}
