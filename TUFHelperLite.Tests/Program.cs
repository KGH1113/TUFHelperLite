using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TUFHelperLite.Bootstrap;
using TUFHelperLite.Domain.Errors;
using TUFHelperLite.Domain.Jobs;
using TUFHelperLite.Infrastructure.Downloads;
using TUFHelperLite.Integration;

internal static class Program
{
  private static readonly List<string> Failures = new();

  private static int Main()
  {
    string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads");
    string updateRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UpdateTests");
    if (Directory.Exists(root)) Directory.Delete(root, true);
    if (Directory.Exists(updateRoot)) Directory.Delete(updateRoot, true);

    try
    {
      string valid = CreateLevel(root, "tuf-12345", "nested/chart.adofai");
      Check("valid nested TUF cache path", 12345, LevelContextResolver.ResolveTufLevelId(valid));
      Check("case-insensitive extension", 42, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-42", "chart.ADOFAI")));

      Check("null", null, LevelContextResolver.ResolveTufLevelId(null));
      Check("blank", null, LevelContextResolver.ResolveTufLevelId("  "));
      Check("non-adofai", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-12345", "notes.txt")));
      Check("missing file", null, LevelContextResolver.ResolveTufLevelId(
        Path.Combine(root, "tuf-12345", "missing.adofai")));
      Check("outside root", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(AppDomain.CurrentDomain.BaseDirectory, "outside", "chart.adofai")));
      Check("root prefix confusion", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root + "-other", "tuf-12345", "chart.adofai")));
      Check("URL cache", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "url-abcdef", "chart.adofai")));
      Check("malformed missing ID", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-", "chart.adofai")));
      Check("malformed nonnumeric ID", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-12x", "chart.adofai")));
      Check("overflow ID", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-999999999999", "chart.adofai")));
      Check("nonpositive ID", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-0", "chart.adofai")));
      Check("file directly under root", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "", "chart.adofai")));

      RunDiskSpacePolicyTests();
      CheckString(
        "bootstrap assembly identity",
        "TUFHelperLite.Bootstrap",
        typeof(Loader).Assembly.GetName().Name);
      RunUpdateTests(updateRoot);

      if (Failures.Count == 0)
      {
        Console.WriteLine("All tests passed.");
        return 0;
      }

      foreach (string failure in Failures) Console.Error.WriteLine(failure);
      return 1;
    }
    finally
    {
      if (Directory.Exists(root)) Directory.Delete(root, true);
      if (Directory.Exists(updateRoot)) Directory.Delete(updateRoot, true);
      string outside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "outside");
      if (Directory.Exists(outside)) Directory.Delete(outside, true);
      string sibling = root + "-other";
      if (Directory.Exists(sibling)) Directory.Delete(sibling, true);
    }
  }

  private static string CreateLevel(string root, string directory, string relativePath)
  {
    string path = Path.Combine(root, directory, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "{}");
    return Path.GetFullPath(path);
  }

  private static void RunDiskSpacePolicyTests()
  {
    const long gibibyte = 1024L * 1024 * 1024;
    const long mebibyte = 1024L * 1024;

    CheckLong("minimum disk reserve", gibibyte, DiskSpacePolicy.CalculateReserveBytes(10 * gibibyte));
    CheckLong("percentage disk reserve", 2 * gibibyte, DiskSpacePolicy.CalculateReserveBytes(40 * gibibyte));
    CheckLong("maximum disk reserve", 5 * gibibyte, DiskSpacePolicy.CalculateReserveBytes(200 * gibibyte));

    long requiredBytes = 512 * mebibyte;
    long exactAvailableBytes = gibibyte + requiredBytes;
    CheckTrue("exact disk-space boundary", DiskSpacePolicy.HasSufficientSpace(
      exactAvailableBytes, 10 * gibibyte, requiredBytes));
    CheckFalse("one byte below disk-space boundary", DiskSpacePolicy.HasSufficientSpace(
      exactAvailableBytes - 1, 10 * gibibyte, requiredBytes));

    CheckLong("unknown download length fallback", 0, DiskSpacePolicy.CalculateRemainingBytes(-1, 64 * mebibyte));
    CheckLong("known download remaining bytes", 60, DiskSpacePolicy.CalculateRemainingBytes(100, 40));
    CheckLong("completed download remaining bytes", 0, DiskSpacePolicy.CalculateRemainingBytes(100, 100));

    DownloadJob job = new("tuf", "12345", "https://example.com/level.zip", "tuf-12345", false);
    job.Fail(new InsufficientDiskSpaceException("Not enough storage space.", 512, 2048));
    DownloadJobSnapshot snapshot = job.Snapshot();
    CheckString("disk failure error code", "insufficient_disk_space", snapshot.ErrorCode);
    CheckLong("disk failure available bytes", 512, snapshot.ErrorAvailableBytes);
    CheckLong("disk failure required bytes", 2048, snapshot.ErrorRequiredBytes);
  }

  private static void RunUpdateTests(string root)
  {
    Directory.CreateDirectory(root);
    CheckTrue("version with v prefix", UpdateVersion.IsNewer("v0.2.0", "0.1.0"));
    CheckFalse("same update version", UpdateVersion.IsNewer("0.1.0", "0.1.0"));
    CheckFalse("older update version", UpdateVersion.IsNewer("0.0.9", "0.1.0"));
    CheckFalse("invalid update version", UpdateVersion.IsNewer("preview", "0.1.0"));

    string releaseJson = "{" +
      "\"tag_name\":\"v0.2.0\",\"draft\":false,\"prerelease\":false,\"assets\":[" +
      "{\"name\":\"TUFHelperLite.zip\",\"browser_download_url\":\"https://github.com/KGH1113/TUFHelperLite/releases/download/v0.2.0/TUFHelperLite.zip\",\"size\":1024}," +
      "{\"name\":\"TUFHelperLite.zip.sha256\",\"browser_download_url\":\"https://github.com/KGH1113/TUFHelperLite/releases/download/v0.2.0/TUFHelperLite.zip.sha256\",\"size\":80}]}";
    BootstrapUpdateService.UpdateReleaseSelection selection = BootstrapUpdateService.SelectRelease(releaseJson, "0.1.0");
    CheckString("release metadata version", "0.2.0", selection?.Version);
    CheckTrue("current release skipped", BootstrapUpdateService.SelectRelease(releaseJson, "0.2.0") == null);
    CheckTrue(
      "prerelease skipped",
      BootstrapUpdateService.SelectRelease(releaseJson.Replace("\"prerelease\":false", "\"prerelease\":true"), "0.1.0") == null);
    ExpectThrows<InvalidDataException>(
      "missing checksum release asset",
      () => BootstrapUpdateService.SelectRelease(
        releaseJson.Replace("TUFHelperLite.zip.sha256", "other.sha256"),
        "0.1.0"));

    string checksum = new string('a', 64);
    CheckString("checksum parsing", checksum, UpdatePackageStager.ParseChecksum(checksum + "  TUFHelperLite.zip"));
    ExpectThrows<InvalidDataException>(
      "invalid checksum",
      () => UpdatePackageStager.ParseChecksum("not-a-checksum"));

    string modRoot = Path.Combine(root, "Mod");
    Directory.CreateDirectory(Path.Combine(modRoot, "Data"));
    File.WriteAllText(Path.Combine(modRoot, "TUFHelperLite.Core.dll"), "old-core");
    File.WriteAllText(Path.Combine(modRoot, "Info.json"), "{\"Version\":\"0.1.0\"}");
    File.WriteAllText(Path.Combine(modRoot, "AdofaiIpcBootstrap.json"), "{}");
    File.WriteAllText(Path.Combine(modRoot, "Data", "user-data.json"), "preserve-me");

    string firstArchive = CreateUpdateArchive(root, "first.zip", "0.2.0", "new-core");
    UpdatePackageStager.Stage(
      firstArchive,
      modRoot,
      "v0.2.0",
      UpdatePackageStager.ComputeSha256(firstArchive));
    CheckTrue("pending update manifest", File.Exists(Path.Combine(modRoot, "Data", "updates", "pending", "pending.json")));

    PendingUpdateInstaller installer = new(modRoot);
    AppliedUpdate firstUpdate = installer.ApplyPending("0.1.0");
    CheckTrue("pending update applied", firstUpdate != null);
    CheckString("updated core file", "new-core", File.ReadAllText(Path.Combine(modRoot, "TUFHelperLite.Core.dll")));
    CheckString("user data preserved", "preserve-me", File.ReadAllText(Path.Combine(modRoot, "Data", "user-data.json")));
    installer.Commit(firstUpdate);
    CheckFalse("pending update removed after commit", Directory.Exists(Path.Combine(modRoot, "Data", "updates", "pending")));

    string rollbackArchive = CreateUpdateArchive(root, "rollback.zip", "0.3.0", "rollback-core");
    UpdatePackageStager.Stage(
      rollbackArchive,
      modRoot,
      "0.3.0",
      UpdatePackageStager.ComputeSha256(rollbackArchive));
    AppliedUpdate rollbackUpdate = installer.ApplyPending("0.2.0");
    installer.Rollback(rollbackUpdate);
    CheckString("core restored after rollback", "new-core", File.ReadAllText(Path.Combine(modRoot, "TUFHelperLite.Core.dll")));
    CheckTrue(
      "failed update quarantined",
      Directory.GetDirectories(Path.Combine(modRoot, "Data", "updates"), "failed-*").Length > 0);

    string updatesRoot = Path.Combine(modRoot, "Data", "updates");
    string untouchedBackup = Path.Combine(updatesRoot, "backup-not-started");
    File.WriteAllText(
      Path.Combine(updatesRoot, "applying.json"),
      $"{{\"Version\":\"0.4.0\",\"BackupRoot\":\"{EscapeJson(untouchedBackup)}\",\"Files\":[{{\"Path\":\"TUFHelperLite.Core.dll\",\"HadOriginal\":true}}]}}");
    installer.RecoverInterruptedApply();
    CheckString(
      "journal before backup preserves original",
      "new-core",
      File.ReadAllText(Path.Combine(modRoot, "TUFHelperLite.Core.dll")));

    string unsafeArchive = CreateInvalidUpdateArchive(root, "unsafe.zip", "TUFHelperLite/../escape.txt", false);
    ExpectThrows<InvalidDataException>(
      "zip path traversal",
      () => UpdatePackageStager.Stage(
        unsafeArchive,
        modRoot,
        "0.4.0",
        UpdatePackageStager.ComputeSha256(unsafeArchive)));

    string symlinkArchive = CreateInvalidUpdateArchive(root, "symlink.zip", "TUFHelperLite/Assets/link", true);
    ExpectThrows<InvalidDataException>(
      "zip symbolic link",
      () => UpdatePackageStager.Stage(
        symlinkArchive,
        modRoot,
        "0.4.0",
        UpdatePackageStager.ComputeSha256(symlinkArchive)));

    string staleArchive = CreateUpdateArchive(root, "stale.zip", "0.1.0", "stale-core");
    UpdatePackageStager.Stage(
      staleArchive,
      modRoot,
      "0.1.0",
      UpdatePackageStager.ComputeSha256(staleArchive));
    CheckTrue("stale pending skipped", installer.ApplyPending("0.2.0") == null);
    CheckString("stale pending preserves core", "new-core", File.ReadAllText(Path.Combine(modRoot, "TUFHelperLite.Core.dll")));

    RunBootstrapUpdateServiceTests(root);
  }

  private static void RunBootstrapUpdateServiceTests(string root)
  {
    CheckLong("bootstrap network timeout", 20, (long)Loader.UpdateNetworkTimeout.TotalSeconds);

    string modRoot = Path.Combine(root, "NetworkMod");
    Directory.CreateDirectory(Path.Combine(modRoot, "Data"));
    File.WriteAllText(Path.Combine(modRoot, "TUFHelperLite.Core.dll"), "old-core");
    File.WriteAllText(Path.Combine(modRoot, "Info.json"), "{\"Version\":\"0.1.0\"}");
    File.WriteAllText(Path.Combine(modRoot, "AdofaiIpcBootstrap.json"), "{}");
    PendingUpdateInstaller installer = new(modRoot);

    string archivePath = CreateUpdateArchive(root, "network.zip", "0.5.0", "network-core");
    byte[] archiveBytes = File.ReadAllBytes(archivePath);
    string archiveSha256 = UpdatePackageStager.ComputeSha256(archivePath);
    string releaseJson = CreateReleaseJson("0.5.0", archiveBytes.Length);
    int packageRequests = 0;

    using (HttpClient client = new(new FakeHttpMessageHandler((request, _) =>
    {
      if (request.RequestUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        return Task.FromResult(Response(releaseJson));
      if (request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
        return Task.FromResult(Response(archiveSha256));
      packageRequests++;
      return Task.FromResult(Response(archiveBytes));
    })))
    using (BootstrapUpdateService updater = new(modRoot, _ => { }, client))
    {
      string staged = updater.CheckAndStageAsync("0.1.0", installer, CancellationToken.None).GetAwaiter().GetResult();
      CheckString("bootstrap update staged", "0.5.0", staged);
      CheckLong("bootstrap package downloaded once", 1, packageRequests);
    }

    packageRequests = 0;
    using (HttpClient client = new(new FakeHttpMessageHandler((request, _) =>
    {
      if (request.RequestUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        return Task.FromResult(Response(releaseJson));
      packageRequests++;
      throw new InvalidOperationException("A valid pending update must be reused.");
    })))
    using (BootstrapUpdateService updater = new(modRoot, _ => { }, client))
    {
      string staged = updater.CheckAndStageAsync("0.1.0", installer, CancellationToken.None).GetAwaiter().GetResult();
      CheckString("matching pending reused", "0.5.0", staged);
      CheckLong("matching pending skips package", 0, packageRequests);
    }

    File.WriteAllText(
      Path.Combine(modRoot, "Data", "updates", "pending", "payload", "TUFHelperLite.Core.dll"),
      "corrupt-core");
    packageRequests = 0;
    using (HttpClient client = new(new FakeHttpMessageHandler((request, _) =>
    {
      if (request.RequestUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        return Task.FromResult(Response(releaseJson));
      if (request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
        return Task.FromResult(Response(archiveSha256));
      packageRequests++;
      return Task.FromResult(Response(archiveBytes));
    })))
    using (BootstrapUpdateService updater = new(modRoot, _ => { }, client))
    {
      string staged = updater.CheckAndStageAsync("0.1.0", installer, CancellationToken.None).GetAwaiter().GetResult();
      CheckString("corrupt pending replaced", "0.5.0", staged);
      CheckLong("corrupt pending redownloads package", 1, packageRequests);
    }

    AppliedUpdate applied = installer.ApplyPending("0.1.0");
    CheckTrue("bootstrap staged update applied", applied != null);
    CheckString("bootstrap updated core before load", "network-core", File.ReadAllText(Path.Combine(modRoot, "TUFHelperLite.Core.dll")));
    installer.Commit(applied);

    using (HttpClient client = new(new FakeHttpMessageHandler((_, _) => Task.FromResult(Response(releaseJson)))))
    using (BootstrapUpdateService updater = new(modRoot, _ => { }, client))
    {
      CheckTrue(
        "current bootstrap release skips download",
        updater.CheckAndStageAsync("0.5.0", installer, CancellationToken.None).GetAwaiter().GetResult() == null);
    }

    using (HttpClient client = new(new FakeHttpMessageHandler(async (_, cancellationToken) =>
    {
      await Task.Delay(Timeout.Infinite, cancellationToken);
      return Response(string.Empty);
    })))
    using (BootstrapUpdateService updater = new(modRoot, _ => { }, client))
    using (CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(20)))
    {
      ExpectThrows<OperationCanceledException>(
        "bootstrap network cancellation",
        () => updater.CheckAndStageAsync("0.5.0", installer, timeout.Token).GetAwaiter().GetResult());
    }

    string mismatchArchive = CreateUpdateArchive(root, "mismatch.zip", "0.6.0", "bad-core");
    byte[] mismatchBytes = File.ReadAllBytes(mismatchArchive);
    string mismatchRelease = CreateReleaseJson("0.6.0", mismatchBytes.Length);
    using (HttpClient client = new(new FakeHttpMessageHandler((request, _) =>
    {
      if (request.RequestUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        return Task.FromResult(Response(mismatchRelease));
      if (request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
        return Task.FromResult(Response(new string('0', 64)));
      return Task.FromResult(Response(mismatchBytes));
    })))
    using (BootstrapUpdateService updater = new(modRoot, _ => { }, client))
    {
      ExpectThrows<InvalidDataException>(
        "bootstrap checksum mismatch",
        () => updater.CheckAndStageAsync("0.5.0", installer, CancellationToken.None).GetAwaiter().GetResult());
    }

    CheckFalse(
      "bootstrap temporary archive cleaned",
      Directory.GetFiles(Path.Combine(modRoot, "Data", "updates"), "download-*.zip").Length > 0);
  }

  private static string CreateReleaseJson(string version, long packageSize)
  {
    string root = $"https://github.com/KGH1113/TUFHelperLite/releases/download/v{version}/";
    return "{" +
      $"\"tag_name\":\"v{version}\",\"draft\":false,\"prerelease\":false,\"assets\":[" +
      $"{{\"name\":\"TUFHelperLite.zip\",\"browser_download_url\":\"{root}TUFHelperLite.zip\",\"size\":{packageSize}}}," +
      $"{{\"name\":\"TUFHelperLite.zip.sha256\",\"browser_download_url\":\"{root}TUFHelperLite.zip.sha256\",\"size\":64}}]}}";
  }

  private static HttpResponseMessage Response(string value)
  {
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(value) };
  }

  private static HttpResponseMessage Response(byte[] value)
  {
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
  }

  private static string CreateUpdateArchive(
    string root,
    string fileName,
    string version,
    string coreContent)
  {
    string path = Path.Combine(root, fileName);
    using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
    using ZipArchive archive = new(stream, ZipArchiveMode.Create);
    AddZipText(archive, "TUFHelperLite/TUFHelperLite.dll", "stable-bootstrap");
    AddZipText(archive, "TUFHelperLite/AdofaiIpc.Bootstrap.dll", "dependency-bootstrap");
    AddZipText(archive, "TUFHelperLite/TUFHelperLite.Core.dll", coreContent);
    AddZipText(archive, "TUFHelperLite/Info.json", $"{{\"Version\":\"{version}\"}}");
    AddZipText(archive, "TUFHelperLite/AdofaiIpcBootstrap.json", "{}");
    AddZipText(archive, "TUFHelperLite/Assets/update.txt", version);
    return path;
  }

  private static string CreateInvalidUpdateArchive(
    string root,
    string fileName,
    string entryName,
    bool symbolicLink)
  {
    string path = Path.Combine(root, fileName);
    using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
    using ZipArchive archive = new(stream, ZipArchiveMode.Create);
    ZipArchiveEntry entry = archive.CreateEntry(entryName);
    if (symbolicLink) entry.ExternalAttributes = 0xA000 << 16;
    using StreamWriter writer = new(entry.Open());
    writer.Write("invalid");
    return path;
  }

  private static void AddZipText(ZipArchive archive, string path, string value)
  {
    ZipArchiveEntry entry = archive.CreateEntry(path);
    using StreamWriter writer = new(entry.Open());
    writer.Write(value);
  }

  private static string EscapeJson(string value)
  {
    return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
  }

  private static void Check(string name, int? expected, int? actual)
  {
    if (expected != actual) Failures.Add($"{name}: expected {expected?.ToString() ?? "null"}, got {actual?.ToString() ?? "null"}");
  }

  private static void CheckLong(string name, long expected, long actual)
  {
    if (expected != actual) Failures.Add($"{name}: expected {expected}, got {actual}");
  }

  private static void CheckString(string name, string expected, string actual)
  {
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
      Failures.Add($"{name}: expected {expected}, got {actual}");
  }

  private static void ExpectThrows<TException>(string name, Action action) where TException : Exception
  {
    try
    {
      action();
      Failures.Add($"{name}: expected {typeof(TException).Name}");
    }
    catch (TException)
    {
    }
    catch (Exception exception)
    {
      Failures.Add($"{name}: expected {typeof(TException).Name}, got {exception.GetType().Name}");
    }
  }

  private static void CheckTrue(string name, bool actual)
  {
    if (!actual) Failures.Add($"{name}: expected true, got false");
  }

  private static void CheckFalse(string name, bool actual)
  {
    if (actual) Failures.Add($"{name}: expected false, got true");
  }

  private sealed class FakeHttpMessageHandler : HttpMessageHandler
  {
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
      _send = send;
    }

    protected override Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request,
      CancellationToken cancellationToken)
    {
      return _send(request, cancellationToken);
    }
  }
}
