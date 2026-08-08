using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace TUFHelperLite.Bootstrap;

internal sealed class PendingUpdateInstaller
{
  private const string UpdatesRelativePath = "Data/updates";
  private const string PendingDirectoryName = "pending";
  private const string ManifestFileName = "pending.json";
  private const string PayloadDirectoryName = "payload";
  private const string JournalFileName = "applying.json";
  private readonly string _modRoot;
  private readonly string _updatesRoot;
  private readonly Action<string> _log;
  private readonly Action<string> _warning;

  public PendingUpdateInstaller(
    string modRoot,
    Action<string> log = null,
    Action<string> warning = null)
  {
    _modRoot = EnsureTrailingSeparator(Path.GetFullPath(modRoot));
    _updatesRoot = Path.Combine(_modRoot, UpdatesRelativePath.Replace('/', Path.DirectorySeparatorChar));
    _log = log ?? (_ => { });
    _warning = warning ?? (_ => { });
  }

  public void RecoverInterruptedApply()
  {
    string journalPath = Path.Combine(_updatesRoot, JournalFileName);
    if (!File.Exists(journalPath)) return;

    ApplyJournal journal = ReadJson<ApplyJournal>(journalPath);
    RollbackJournal(journal);
    TryDeleteFile(journalPath);
    QuarantinePending("interrupted");
    _warning("Recovered an interrupted TUFHelperLite update.");
  }

  public bool HasValidPending(string version)
  {
    string pendingRoot = Path.Combine(_updatesRoot, PendingDirectoryName);
    string manifestPath = Path.Combine(pendingRoot, ManifestFileName);
    if (!File.Exists(manifestPath)) return false;

    try
    {
      PendingUpdateManifest manifest = ReadJson<PendingUpdateManifest>(manifestPath);
      if (!UpdateVersion.TryParse(manifest?.Version, out Version pendingVersion) ||
          !UpdateVersion.TryParse(version, out Version expectedVersion) ||
          pendingVersion != expectedVersion)
        return false;

      ValidateManifest(manifest, pendingRoot);
      return true;
    }
    catch (Exception exception)
    {
      QuarantinePending("invalid");
      _warning("Ignored an invalid TUFHelperLite update: " + exception.Message);
      return false;
    }
  }

  public AppliedUpdate ApplyPending(string currentVersion)
  {
    string pendingRoot = Path.Combine(_updatesRoot, PendingDirectoryName);
    string manifestPath = Path.Combine(pendingRoot, ManifestFileName);
    if (!File.Exists(manifestPath)) return null;

    try
    {
      PendingUpdateManifest manifest = ReadJson<PendingUpdateManifest>(manifestPath);
      ValidateManifest(manifest, pendingRoot);
      if (!UpdateVersion.IsNewer(manifest.Version, currentVersion))
      {
        QuarantinePending("stale");
        _log($"Ignored stale TUFHelperLite {manifest.Version} update files.");
        return null;
      }

      ApplyJournal journal = new()
      {
        Version = manifest.Version,
        BackupRoot = Path.Combine(_updatesRoot, "backup-" + Guid.NewGuid().ToString("N")),
        Files = new List<AppliedFile>()
      };
      Directory.CreateDirectory(journal.BackupRoot);
      string journalPath = Path.Combine(_updatesRoot, JournalFileName);

      try
      {
        PendingUpdateFile bootstrap = manifest.Files.Single(file =>
          UpdatePackageStager.IsDependencyBootstrapCandidate(file.Path));
        string bootstrapSource = ResolveInside(Path.Combine(pendingRoot, PayloadDirectoryName), bootstrap.Path);
        journal.BootstrapTrial = DependencyBootstrapShim.Stage(_modRoot, bootstrapSource);
        WriteJsonAtomic(journalPath, journal);

        foreach (PendingUpdateFile file in manifest.Files
                   .Where(item => !UpdatePackageStager.IsDependencyBootstrapCandidate(item.Path))
                   .OrderBy(item => item.Path, StringComparer.Ordinal))
        {
          ApplyFile(pendingRoot, file, journal, journalPath);
        }
      }
      catch
      {
        RollbackJournal(journal);
        TryDeleteFile(journalPath);
        QuarantinePending("apply-failed");
        throw;
      }

      _log($"Applied pending TUFHelperLite {manifest.Version} update files.");
      return new AppliedUpdate(manifest.Version, journal);
    }
    catch (Exception exception)
    {
      QuarantinePending("invalid");
      _warning("Ignored an invalid TUFHelperLite update: " + exception.Message);
      return null;
    }
  }

  public void Commit(AppliedUpdate update)
  {
    if (update == null) return;
    TryDeleteDirectory(update.Journal.BackupRoot);
    TryDeleteDirectory(Path.Combine(_updatesRoot, PendingDirectoryName));
    TryDeleteFile(Path.Combine(_updatesRoot, JournalFileName));
    _log($"TUFHelperLite {update.Version} update completed.");
  }

  public void Rollback(AppliedUpdate update)
  {
    if (update == null) return;
    RollbackJournal(update.Journal);
    TryDeleteFile(Path.Combine(_updatesRoot, JournalFileName));
    QuarantinePending("load-failed");
    _warning($"Rolled back TUFHelperLite {update.Version} after a load failure.");
  }

  private void ApplyFile(
    string pendingRoot,
    PendingUpdateFile file,
    ApplyJournal journal,
    string journalPath)
  {
    string relativePath = NormalizeAndValidatePath(file.Path);
    string source = ResolveInside(Path.Combine(pendingRoot, PayloadDirectoryName), relativePath);
    string destination = ResolveInside(_modRoot, relativePath);
    string backup = ResolveInside(journal.BackupRoot, relativePath);

    AppliedFile applied = new()
    {
      Path = relativePath,
      HadOriginal = File.Exists(destination)
    };
    journal.Files.Add(applied);
    WriteJsonAtomic(journalPath, journal);

    Directory.CreateDirectory(Path.GetDirectoryName(destination));
    if (applied.HadOriginal)
    {
      Directory.CreateDirectory(Path.GetDirectoryName(backup));
      File.Move(destination, backup);
    }

    File.Move(source, destination);
  }

  private void ValidateManifest(PendingUpdateManifest manifest, string pendingRoot)
  {
    if (manifest == null || !TryParseVersion(manifest.Version, out _))
      throw new InvalidDataException("Pending update version is invalid.");
    if (!IsSha256(manifest.ArchiveSha256))
      throw new InvalidDataException("Pending update archive checksum is invalid.");
    if (manifest.Files == null || manifest.Files.Count == 0)
      throw new InvalidDataException("Pending update has no files.");

    HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
    foreach (PendingUpdateFile file in manifest.Files)
    {
      string relativePath = NormalizeAndValidatePath(file.Path);
      if (!paths.Add(relativePath)) throw new InvalidDataException("Pending update contains duplicate files.");
      if (file.Length < 0 || !IsSha256(file.Sha256))
        throw new InvalidDataException("Pending update file metadata is invalid.");

      string source = ResolveInside(Path.Combine(pendingRoot, PayloadDirectoryName), relativePath);
      FileInfo info = new(source);
      if (!info.Exists || info.Length != file.Length || !ComputeSha256(source).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Pending update file verification failed: " + relativePath);
    }
  }

  private void RollbackJournal(ApplyJournal journal)
  {
    if (journal?.Files == null) return;

    foreach (AppliedFile file in journal.Files.AsEnumerable().Reverse())
    {
      string relativePath = NormalizeAndValidatePath(file.Path);
      string destination = ResolveInside(_modRoot, relativePath);
      string backup = ResolveInside(journal.BackupRoot, relativePath);
      if (file.HadOriginal)
      {
        if (File.Exists(backup))
        {
          TryDeleteFile(destination);
          Directory.CreateDirectory(Path.GetDirectoryName(destination));
          File.Move(backup, destination);
        }
      }
      else
      {
        TryDeleteFile(destination);
      }
    }

    if (!string.IsNullOrWhiteSpace(journal.BootstrapTrial))
    {
      try { DependencyBootstrapShim.Discard(_modRoot, journal.BootstrapTrial); }
      catch (Exception exception) { _warning("Could not discard dependency bootstrap trial: " + exception.Message); }
    }

    TryDeleteDirectory(journal.BackupRoot);
  }

  private string NormalizeAndValidatePath(string value)
  {
    if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Update path is empty.");
    string path = value.Replace('\\', '/').TrimStart('/');
    if (path.Split('/').Any(part => part.Length == 0 || part == "." || part == ".."))
      throw new InvalidDataException("Update path is unsafe: " + value);

    bool allowed = path.Equals("TUFHelperLite.Core.dll", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("Info.json", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("AdofaiIpcBootstrap.json", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("THIRD_PARTY_NOTICES.md", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                   UpdatePackageStager.IsDependencyBootstrapCandidate(path);
    if (!allowed) throw new InvalidDataException("Update path is not allowed: " + value);
    return path;
  }

  private void QuarantinePending(string reason)
  {
    string pending = Path.Combine(_updatesRoot, PendingDirectoryName);
    if (!Directory.Exists(pending)) return;
    string failed = Path.Combine(
      _updatesRoot,
      $"failed-{DateTime.UtcNow:yyyyMMddHHmmss}-{reason}-{Guid.NewGuid():N}");
    try { Directory.Move(pending, failed); }
    catch (Exception exception) { _warning("Could not quarantine the pending update: " + exception.Message); }
  }

  private static string ResolveInside(string root, string relativePath)
  {
    string normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
    string resolved = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    if (!resolved.StartsWith(normalizedRoot, StringComparison.Ordinal))
      throw new InvalidDataException("Update path escapes its root.");
    return resolved;
  }

  private static T ReadJson<T>(string path)
  {
    T value = JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
    if (value == null) throw new InvalidDataException("Update metadata is empty or invalid.");
    return value;
  }

  private static void WriteJsonAtomic(string path, object value)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    string temporary = path + ".tmp";
    File.WriteAllText(temporary, JsonConvert.SerializeObject(value, Formatting.Indented));
    if (File.Exists(path)) File.Replace(temporary, path, null);
    else File.Move(temporary, path);
  }

  private static bool TryParseVersion(string value, out Version version)
  {
    version = null;
    if (string.IsNullOrWhiteSpace(value)) return false;
    string normalized = value.Trim();
    if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(1);
    return Version.TryParse(normalized, out version);
  }

  private static bool IsSha256(string value)
  {
    return value != null && value.Length == 64 && value.All(Uri.IsHexDigit);
  }

  private static string ComputeSha256(string path)
  {
    using SHA256 sha256 = SHA256.Create();
    using FileStream stream = File.OpenRead(path);
    return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
  }

  private static string EnsureTrailingSeparator(string path)
  {
    return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
      ? path
      : path + Path.DirectorySeparatorChar;
  }

  private static void TryDeleteFile(string path)
  {
    if (File.Exists(path)) File.Delete(path);
  }

  private static void TryDeleteDirectory(string path)
  {
    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Directory.Delete(path, true);
  }
}

internal sealed class AppliedUpdate
{
  public AppliedUpdate(string version, ApplyJournal journal)
  {
    Version = version;
    Journal = journal;
  }

  public string Version { get; }
  public ApplyJournal Journal { get; }
}

internal sealed class PendingUpdateManifest
{
  public string Version { get; set; }
  public string ArchiveSha256 { get; set; }
  public List<PendingUpdateFile> Files { get; set; }
}

internal sealed class PendingUpdateFile
{
  public string Path { get; set; }
  public long Length { get; set; }
  public string Sha256 { get; set; }
}

internal sealed class ApplyJournal
{
  public string Version { get; set; }
  public string BackupRoot { get; set; }
  public string BootstrapTrial { get; set; }
  public List<AppliedFile> Files { get; set; }
}

internal sealed class AppliedFile
{
  public string Path { get; set; }
  public bool HadOriginal { get; set; }
}
