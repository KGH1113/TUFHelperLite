using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
namespace TUFHelperLite.Bootstrap;

internal static class UpdatePackageStager
{
  internal const long MaximumArchiveBytes = 256L * 1024 * 1024;
  internal const long MaximumExtractedBytes = 512L * 1024 * 1024;
  private const int MaximumEntries = 10000;
  private const string PackageRoot = "TUFHelperLite/";

  public static void Stage(
    string archivePath,
    string modRoot,
    string expectedVersion,
    string archiveSha256)
  {
    if (!IsSha256(archiveSha256)) throw new InvalidDataException("Update checksum is invalid.");
    FileInfo archiveInfo = new(archivePath);
    if (!archiveInfo.Exists || archiveInfo.Length <= 0 || archiveInfo.Length > MaximumArchiveBytes)
      throw new InvalidDataException("Update archive size is invalid.");
    if (!ComputeSha256(archivePath).Equals(archiveSha256, StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("Update archive checksum does not match.");

    string updatesRoot = Path.Combine(Path.GetFullPath(modRoot), "Data", "updates");
    string stageRoot = Path.Combine(updatesRoot, "stage-" + Guid.NewGuid().ToString("N"));
    string payloadRoot = Path.Combine(stageRoot, "payload");
    string pendingRoot = Path.Combine(updatesRoot, "pending");
    Directory.CreateDirectory(payloadRoot);

    try
    {
      List<StagedUpdateFile> files = ExtractAllowedFiles(
        archivePath,
        payloadRoot,
        Path.GetFullPath(modRoot));
      ValidateRequiredFiles(payloadRoot, files, expectedVersion);

      PendingUpdateManifest manifest = new()
      {
        Version = UpdateVersion.Normalize(expectedVersion),
        ArchiveSha256 = archiveSha256.ToLowerInvariant(),
        Files = files
          .OrderBy(file => file.Path, StringComparer.Ordinal)
          .Select(file => new PendingUpdateFile
          {
            Path = file.Path,
            Length = file.Length,
            Sha256 = file.Sha256
          })
          .ToList()
      };
      File.WriteAllText(
        Path.Combine(stageRoot, "pending.json"),
        JsonConvert.SerializeObject(manifest, Formatting.Indented));

      Directory.CreateDirectory(updatesRoot);
      if (Directory.Exists(pendingRoot)) Directory.Delete(pendingRoot, true);
      Directory.Move(stageRoot, pendingRoot);
    }
    finally
    {
      if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, true);
    }
  }

  public static string ParseChecksum(string value)
  {
    if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Update checksum file is empty.");
    string token = value.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    if (!IsSha256(token)) throw new InvalidDataException("Update checksum file is invalid.");
    return token.ToLowerInvariant();
  }

  public static string ComputeSha256(string path)
  {
    using SHA256 sha256 = SHA256.Create();
    using FileStream stream = File.OpenRead(path);
    return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
  }

  private static List<StagedUpdateFile> ExtractAllowedFiles(
    string archivePath,
    string payloadRoot,
    string modRoot)
  {
    using FileStream stream = File.OpenRead(archivePath);
    using ZipArchive archive = new(stream, ZipArchiveMode.Read, false);
    if (archive.Entries.Count > MaximumEntries)
      throw new InvalidDataException("Update archive contains too many entries.");

    long totalLength = 0;
    HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
    List<ZipArchiveEntry> allowedEntries = new();

    foreach (ZipArchiveEntry entry in archive.Entries)
    {
      string packagePath = NormalizeArchivePath(entry.FullName);
      if (string.IsNullOrEmpty(packagePath) || string.IsNullOrEmpty(entry.Name)) continue;
      if (IsSymbolicLink(entry)) throw new InvalidDataException("Update archive contains a symbolic link.");

      if (IsIgnoredPackageFile(packagePath)) continue;
      if (!IsAllowedPath(packagePath))
        throw new InvalidDataException("Update archive contains an unexpected file: " + packagePath);
      if (!paths.Add(packagePath))
        throw new InvalidDataException("Update archive contains duplicate files: " + packagePath);

      totalLength = checked(totalLength + entry.Length);
      if (totalLength > MaximumExtractedBytes)
        throw new InvalidDataException("Update archive expands beyond the size limit.");
      allowedEntries.Add(entry);
    }

    UpdateDiskSpacePolicy.EnsureSufficientSpace(
      modRoot,
      totalLength,
      "Not enough free disk space to stage the TUFHelperLite update.");

    List<StagedUpdateFile> files = new();
    long actualTotalLength = 0;
    byte[] buffer = new byte[128 * 1024];
    foreach (ZipArchiveEntry entry in allowedEntries)
    {
      string relativePath = NormalizeArchivePath(entry.FullName);
      string target = ResolveInside(payloadRoot, relativePath);
      Directory.CreateDirectory(Path.GetDirectoryName(target));
      using (Stream input = entry.Open())
      using (FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
      {
        long entryLength = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
          entryLength = checked(entryLength + read);
          actualTotalLength = checked(actualTotalLength + read);
          if (entryLength > entry.Length || actualTotalLength > MaximumExtractedBytes)
            throw new InvalidDataException("Update archive expands beyond the size limit.");
          output.Write(buffer, 0, read);
        }
        if (entryLength != entry.Length)
          throw new InvalidDataException("Update archive entry length does not match its metadata.");
      }
      files.Add(new StagedUpdateFile
      {
        Path = relativePath,
        Length = entry.Length,
        Sha256 = ComputeSha256(target)
      });
    }

    return files;
  }

  private static void ValidateRequiredFiles(
    string payloadRoot,
    IReadOnlyCollection<StagedUpdateFile> files,
    string expectedVersion)
  {
    if (!files.Any(file => file.Path.Equals("TUFHelperLite.Core.dll", StringComparison.OrdinalIgnoreCase)))
      throw new InvalidDataException("Update package is missing TUFHelperLite.Core.dll.");
    if (!files.Any(file => file.Path.Equals("Info.json", StringComparison.OrdinalIgnoreCase)))
      throw new InvalidDataException("Update package is missing Info.json.");
    if (!files.Any(file => file.Path.Equals("AdofaiIpcBootstrap.json", StringComparison.OrdinalIgnoreCase)))
      throw new InvalidDataException("Update package is missing AdofaiIpcBootstrap.json.");

    string infoPath = ResolveInside(payloadRoot, "Info.json");
    PackageInfo info = JsonConvert.DeserializeObject<PackageInfo>(File.ReadAllText(infoPath));
    if (info == null || !UpdateVersion.TryParse(info.Version, out Version packageVersion) ||
        !UpdateVersion.TryParse(expectedVersion, out Version releaseVersion) || packageVersion != releaseVersion)
      throw new InvalidDataException("Update package version does not match the release tag.");
  }

  private static string NormalizeArchivePath(string value)
  {
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    string path = value.Replace('\\', '/');
    if (path.StartsWith("/", StringComparison.Ordinal) ||
        path.Split('/').Any(part => part == "." || part == ".."))
      throw new InvalidDataException("Update archive path is unsafe: " + value);
    if (!path.StartsWith(PackageRoot, StringComparison.Ordinal))
      throw new InvalidDataException("Update archive must use the TUFHelperLite package root.");
    return path.Substring(PackageRoot.Length).TrimEnd('/');
  }

  private static bool IsAllowedPath(string path)
  {
    return path.Equals("TUFHelperLite.Core.dll", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("Info.json", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("AdofaiIpcBootstrap.json", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("THIRD_PARTY_NOTICES.md", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
  }

  private static bool IsIgnoredPackageFile(string path)
  {
    return path.Equals("TUFHelperLite.dll", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("AdofaiIpc.Bootstrap.dll", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("TUFHelperLite.pdb", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("TUFHelperLite.Core.pdb", StringComparison.OrdinalIgnoreCase);
  }

  private static bool IsSymbolicLink(ZipArchiveEntry entry)
  {
    const int symbolicLinkMode = 0xA000;
    return ((entry.ExternalAttributes >> 16) & 0xF000) == symbolicLinkMode;
  }

  private static string ResolveInside(string root, string relativePath)
  {
    string normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
    string resolved = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    if (!resolved.StartsWith(normalizedRoot, StringComparison.Ordinal))
      throw new InvalidDataException("Update path escapes its root.");
    return resolved;
  }

  private static bool IsSha256(string value)
  {
    return value != null && value.Length == 64 && value.All(Uri.IsHexDigit);
  }

  private static string EnsureTrailingSeparator(string path)
  {
    return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
      ? path
      : path + Path.DirectorySeparatorChar;
  }

  private sealed class PackageInfo
  {
    public string Version { get; set; }
  }

  private sealed class StagedUpdateFile
  {
    public string Path { get; set; }
    public long Length { get; set; }
    public string Sha256 { get; set; }
  }

  private sealed class PendingUpdateManifest
  {
    public string Version { get; set; }
    public string ArchiveSha256 { get; set; }
    public List<PendingUpdateFile> Files { get; set; }
  }

  private sealed class PendingUpdateFile
  {
    public string Path { get; set; }
    public long Length { get; set; }
    public string Sha256 { get; set; }
  }
}
