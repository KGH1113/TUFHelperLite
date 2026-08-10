using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TUFHelperLite.UpdateEngine;

internal static class RuntimePackageInstaller
{
  internal const long MaximumArchiveBytes = 256L * 1024 * 1024;
  internal const long MaximumExtractedBytes = 512L * 1024 * 1024;
  private const int MaximumEntries = 10000;
  private const string PackageRoot = "TUFHelperLite/";
  private static readonly Regex VersionPattern = new(
    "\\\"Version\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"",
    RegexOptions.CultureInvariant);

  public static string Install(string archivePath, string modRoot, string expectedVersion, string expectedSha256)
  {
    if (!IsSha256(expectedSha256))
      throw new InvalidDataException("TUFHelperLite update checksum is invalid.");
    FileInfo archiveInfo = new(archivePath);
    if (!archiveInfo.Exists || archiveInfo.Length <= 0 || archiveInfo.Length > MaximumArchiveBytes)
      throw new InvalidDataException("TUFHelperLite update archive size is invalid.");
    if (!ComputeSha256(archivePath).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("TUFHelperLite update checksum does not match.");

    string version = NormalizeVersion(expectedVersion);
    string versionsRoot = Path.Combine(Path.GetFullPath(modRoot), "Runtime", "versions");
    string target = Path.Combine(versionsRoot, version);
    string quarantine = null;
    if (Directory.Exists(target))
    {
      try
      {
        ValidateCandidate(target, version);
        return target;
      }
      catch (Exception exception)
      {
        quarantine = target + ".invalid-" + Guid.NewGuid().ToString("N");
        try { Directory.Move(target, quarantine); }
        catch (Exception moveException)
        {
          throw new InvalidDataException(
            "The existing TUFHelperLite runtime candidate is invalid and could not be quarantined.",
            new AggregateException(exception, moveException));
        }
      }
    }

    string staging = target + ".stage-" + Guid.NewGuid().ToString("N");
    try
    {
      Directory.CreateDirectory(staging);
      ExtractCandidate(archivePath, staging, version, modRoot);
      ValidateCandidate(staging, version);
      Directory.CreateDirectory(versionsRoot);
      Directory.Move(staging, target);
      return target;
    }
    finally
    {
      TryDeleteDirectory(staging);
    }
  }

  public static void ValidateCandidate(string root, string expectedVersion)
  {
    string core = Path.Combine(root, "TUFHelperLite.Core.dll");
    string engine = Path.Combine(root, "TUFHelperLite.UpdateEngine.dll");
    string bootstrap = Path.Combine(root, "AdofaiIpc.Bootstrap.dll");
    string info = Path.Combine(root, "Info.json");
    if (!File.Exists(core) || !File.Exists(engine) || !File.Exists(bootstrap) || !File.Exists(info))
      throw new InvalidDataException("The TUFHelperLite runtime candidate is incomplete.");
    ValidateAssembly(core, "TUFHelperLite.Core");
    ValidateAssembly(engine, "TUFHelperLite.UpdateEngine");
    ValidateAssembly(bootstrap, "AdofaiIpc.Bootstrap");
    Match match = VersionPattern.Match(File.ReadAllText(info));
    if (!match.Success || !VersionsEqual(match.Groups[1].Value, expectedVersion))
      throw new InvalidDataException("The TUFHelperLite runtime candidate version is invalid.");
  }

  public static string ParseChecksum(string value)
  {
    string token = value?.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    if (!IsSha256(token))
      throw new InvalidDataException("TUFHelperLite checksum asset is invalid.");
    return token.ToLowerInvariant();
  }

  public static string ComputeSha256(string path)
  {
    using SHA256 sha256 = SHA256.Create();
    using FileStream stream = File.OpenRead(path);
    return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
  }

  private static void ExtractCandidate(string archivePath, string destination, string expectedVersion, string modRoot)
  {
    using FileStream stream = File.OpenRead(archivePath);
    using ZipArchive archive = new(stream, ZipArchiveMode.Read, false);
    if (archive.Entries.Count > MaximumEntries)
      throw new InvalidDataException("TUFHelperLite update archive contains too many entries.");

    HashSet<string> packagePaths = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string> candidatePaths = new(StringComparer.OrdinalIgnoreCase);
    List<(ZipArchiveEntry Entry, string CandidatePath)> candidateEntries = new();
    long expandedBytes = 0;

    foreach (ZipArchiveEntry entry in archive.Entries)
    {
      string path = NormalizeArchivePath(entry.FullName);
      if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(entry.Name)) continue;
      if (IsSymbolicLink(entry))
        throw new InvalidDataException("TUFHelperLite update archive contains a symbolic link.");
      if (!IsLegacyCompatiblePath(path))
        throw new InvalidDataException("TUFHelperLite update archive is not compatible with version 0.1.2: " + path);
      if (!packagePaths.Add(path))
        throw new InvalidDataException("TUFHelperLite update archive contains duplicate files: " + path);

      expandedBytes = checked(expandedBytes + entry.Length);
      if (expandedBytes > MaximumExtractedBytes)
        throw new InvalidDataException("TUFHelperLite update archive expands beyond the size limit.");

      string candidatePath = MapCandidatePath(path);
      if (candidatePath == null) continue;
      if (!candidatePaths.Add(candidatePath))
        throw new InvalidDataException("TUFHelperLite update archive maps duplicate runtime files.");
      candidateEntries.Add((entry, candidatePath));
    }

    string[] controls =
    {
      "Assets/AdofaiIpc/AdofaiIpc.DependencyShim.dll",
      "Assets/AdofaiIpc/AdofaiIpc.Bootstrap.dll",
      "Assets/AdofaiIpc/AdofaiIpc.Migration.dll",
      "Assets/AdofaiIpc/AdofaiIpcBootstrap.json",
      "Assets/AdofaiIpc/TUFHelperLite.Launcher.dll",
      "Assets/AdofaiIpc/TUFHelperLite.UpdateEngine.dll",
    };
    foreach (string control in controls)
      if (!packagePaths.Contains(control))
        throw new InvalidDataException("TUFHelperLite update archive is missing " + control + ".");

    UpdateDiskSpacePolicy.EnsureSufficientSpace(
      modRoot,
      expandedBytes,
      "Not enough free disk space to stage the TUFHelperLite runtime.");

    byte[] buffer = new byte[128 * 1024];
    foreach ((ZipArchiveEntry entry, string candidatePath) in candidateEntries)
    {
      string target = ResolveInside(destination, candidatePath);
      Directory.CreateDirectory(Path.GetDirectoryName(target));
      using Stream input = entry.Open();
      using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
      long written = 0;
      int read;
      while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
      {
        written = checked(written + read);
        if (written > entry.Length)
          throw new InvalidDataException("TUFHelperLite update entry exceeds its declared size.");
        output.Write(buffer, 0, read);
      }
      if (written != entry.Length)
        throw new InvalidDataException("TUFHelperLite update entry length is invalid.");
    }

    string info = Path.Combine(destination, "Info.json");
    Match match = File.Exists(info) ? VersionPattern.Match(File.ReadAllText(info)) : Match.Empty;
    if (!match.Success || !VersionsEqual(match.Groups[1].Value, expectedVersion))
      throw new InvalidDataException("TUFHelperLite package version does not match the release tag.");
  }

  private static string MapCandidatePath(string path)
  {
    if (path.Equals("TUFHelperLite.Core.dll", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("Info.json", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("THIRD_PARTY_NOTICES.md", StringComparison.OrdinalIgnoreCase))
      return path;
    if (path.Equals("Assets/AdofaiIpc/TUFHelperLite.UpdateEngine.dll", StringComparison.OrdinalIgnoreCase))
      return "TUFHelperLite.UpdateEngine.dll";
    if (path.Equals("Assets/AdofaiIpc/AdofaiIpc.Bootstrap.dll", StringComparison.OrdinalIgnoreCase))
      return "AdofaiIpc.Bootstrap.dll";
    if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith("Assets/AdofaiIpc/", StringComparison.OrdinalIgnoreCase))
      return path;
    return null;
  }

  private static bool IsLegacyCompatiblePath(string path)
  {
    return path.Equals("TUFHelperLite.Core.dll", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("Info.json", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("AdofaiIpcBootstrap.json", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("THIRD_PARTY_NOTICES.md", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("TUFHelperLite.dll", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("AdofaiIpc.Bootstrap.dll", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("TUFHelperLite.pdb", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("TUFHelperLite.Core.pdb", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
  }

  private static string NormalizeArchivePath(string value)
  {
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    string path = value.Replace('\\', '/');
    if (path.Equals(PackageRoot, StringComparison.Ordinal))
      return string.Empty;
    path = path.TrimEnd('/');
    if (path.StartsWith("/", StringComparison.Ordinal) ||
        path.Split('/').Any(part => part.Length == 0 || part == "." || part == ".."))
      throw new InvalidDataException("TUFHelperLite update archive path is unsafe: " + value);
    if (!path.StartsWith(PackageRoot, StringComparison.Ordinal))
      throw new InvalidDataException("TUFHelperLite update archive has an invalid package root.");
    return path.Substring(PackageRoot.Length);
  }

  private static string ResolveInside(string root, string relativePath)
  {
    string prefix = EnsureTrailingSeparator(Path.GetFullPath(root));
    string path = Path.GetFullPath(Path.Combine(prefix, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    if (!path.StartsWith(prefix, StringComparison.Ordinal))
      throw new InvalidDataException("TUFHelperLite update path escapes the runtime root.");
    return path;
  }

  private static void ValidateAssembly(string path, string expectedName)
  {
    AssemblyName identity = AssemblyName.GetAssemblyName(path);
    if (!string.Equals(identity.Name, expectedName, StringComparison.Ordinal))
      throw new InvalidDataException("TUFHelperLite runtime assembly identity is invalid: " + path);
  }

  private static string NormalizeVersion(string value)
  {
    string version = value?.Trim().TrimStart('v', 'V');
    SemanticVersion.Parse(version);
    return version;
  }

  private static bool VersionsEqual(string left, string right) =>
    SemanticVersion.Parse(left).CompareTo(SemanticVersion.Parse(right)) == 0;

  private static bool IsSha256(string value) =>
    value != null && value.Length == 64 && value.All(Uri.IsHexDigit);

  private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
    ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

  private static string EnsureTrailingSeparator(string path) =>
    path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
      ? path
      : path + Path.DirectorySeparatorChar;

  private static void TryDeleteDirectory(string path)
  {
    try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
  }
}
