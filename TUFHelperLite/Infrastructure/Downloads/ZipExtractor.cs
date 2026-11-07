using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;

namespace TUFHelperLite.Infrastructure.Downloads;

// Adapted from JALib.Tools.Zipper (BSD-3-Clause).
internal static class ZipExtractor
{
  private const int MaxEntryCount = 50000;
  private const int MaxPathDepth = 32;
  private const long ExpansionAllowanceBytes = 1L * 1024 * 1024 * 1024;
  private const long DiskCheckIntervalBytes = 64L * 1024 * 1024;
  private const int MaxExpansionRatio = 200;
  private const int CopyBufferSize = 128 * 1024;

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
    EnsureZipFormat(stream);
    List<ZipRawEntryName> rawNames = ZipCentralDirectory.TryRead(stream);
    using ZipArchive archive = new(stream, ZipArchiveMode.Read, false, Encoding.UTF8);
    string[] entryNames = ReadEntryNames(archive, rawNames);
    ExtractionPlan plan = ValidateArchive(archive, entryNames, stream.Length, destinationRoot);
    OutputLocation location = new(destinationRoot, destinationPrefix, pathComparison);
    byte[] buffer = new byte[CopyBufferSize];
    long totalWritten = 0;
    long nextDiskCheck = DiskCheckIntervalBytes;

    for (int index = 0; index < archive.Entries.Count; index++)
    {
      cancellationToken.ThrowIfCancellationRequested();

      ZipArchiveEntry entry = archive.Entries[index];
      string entryName = entryNames[index];
      string[] components = ZipEntryPaths.Split(entryName, ZipEntryPaths.IsWindows);
      if (components.Length == 0) continue;

      if (ZipEntryPaths.IsDirectoryEntry(entryName))
      {
        // Files create their own parent folders, so a folder that cannot be made
        // under its archive name is skipped instead of failing the archive.
        TryCreateDirectory(location, components, entryName);
        continue;
      }

      using Stream input = entry.Open();
      using FileStream output = CreateOutputFile(location, components, entryName);
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

  // Some levels on TUF are RAR or 7z files; say so instead of reporting a corrupt zip.
  private static void EnsureZipFormat(Stream stream)
  {
    byte[] header = new byte[6];
    int read = 0;
    int bytesRead;
    while (read < header.Length && (bytesRead = stream.Read(header, read, header.Length - read)) > 0) read += bytesRead;
    stream.Seek(0, SeekOrigin.Begin);

    bool isRar = read >= 6 && header[0] == 'R' && header[1] == 'a' && header[2] == 'r' && header[3] == '!' &&
      header[4] == 0x1A && header[5] == 0x07;
    bool isSevenZip = read >= 6 && header[0] == '7' && header[1] == 'z' && header[2] == 0xBC && header[3] == 0xAF &&
      header[4] == 0x27 && header[5] == 0x1C;
    if (isRar || isSevenZip)
    {
      throw new InvalidDataException(string.Concat(
        "This level is packed as a ", isRar ? "RAR" : "7z", " archive. Only ZIP archives can be opened automatically."));
    }
  }

  private static string[] ReadEntryNames(ZipArchive archive, List<ZipRawEntryName> rawNames)
  {
    List<string> utf8Names = new(archive.Entries.Count);
    foreach (ZipArchiveEntry entry in archive.Entries) utf8Names.Add(entry.FullName);
    return ZipEntryNameDecoder.Decode(utf8Names, rawNames);
  }

  private static FileStream CreateOutputFile(OutputLocation location, string[] components, string entryName)
  {
    Exception firstFailure = null;
    foreach (string[] candidate in ZipEntryPaths.FileCandidates(components, ZipEntryPaths.IsWindows))
    {
      try
      {
        string outputPath = location.Resolve(candidate, entryName);
        string parentDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(parentDirectory)) Directory.CreateDirectory(parentDirectory);
        return new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
      }
      catch (Exception e) when (IsPathFailure(e))
      {
        firstFailure ??= e;
      }
    }

    ExceptionDispatchInfo.Capture(firstFailure).Throw();
    throw firstFailure;
  }

  private static void TryCreateDirectory(OutputLocation location, string[] components, string entryName)
  {
    try
    {
      Directory.CreateDirectory(location.Resolve(components, entryName));
    }
    catch (Exception e) when (IsPathFailure(e))
    {
    }
  }

  // Failures caused by the name or length of a path rather than by the archive data.
  private static bool IsPathFailure(Exception exception) =>
    exception is IOException ||
    exception is UnauthorizedAccessException ||
    exception is NotSupportedException ||
    exception is ArgumentException;

  private static ExtractionPlan ValidateArchive(
    ZipArchive archive,
    string[] entryNames,
    long archiveLength,
    string destinationRoot)
  {
    if (archive.Entries.Count > MaxEntryCount)
    {
      throw new InvalidDataException(string.Concat("Archive contains too many entries (", archive.Entries.Count, ")."));
    }

    long totalLength = 0;
    for (int index = 0; index < archive.Entries.Count; index++)
    {
      string entryName = entryNames[index];
      int depth = entryName.Replace('\\', '/')
        .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
        .Length;
      if (depth > MaxPathDepth)
      {
        throw new InvalidDataException(string.Concat("Archive entry path is too deep: ", entryName));
      }

      totalLength = checked(totalLength + archive.Entries[index].Length);
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

  private sealed class OutputLocation
  {
    private readonly string _root;
    private readonly string _prefix;
    private readonly StringComparison _comparison;

    public OutputLocation(string root, string prefix, StringComparison comparison)
    {
      _root = root;
      _prefix = prefix;
      _comparison = comparison;
    }

    public string Resolve(string[] components, string entryName)
    {
      string outputPath = Path.GetFullPath(Path.Combine(_root, Path.Combine(components)));
      bool isInsideDestination = outputPath.Equals(_root, _comparison) || outputPath.StartsWith(_prefix, _comparison);

      if (!isInsideDestination)
      {
        throw new InvalidDataException("Archive entry escapes the destination directory: " + entryName);
      }

      return outputPath;
    }
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
