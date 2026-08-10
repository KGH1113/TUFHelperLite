using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TUFHelperLite.Launcher;
using TUFHelperLite.UpdateEngine;
using UnityModManagerNet;

internal static class Program
{
  private const string LegacyFixtureSha256 =
    "ffbb08d28d5189528f4d64906d90e22e9f22eb23a433409e158f983c6b31cc55";
  private static readonly List<string> Failures = new();

  private static int Main()
  {
    string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UpdaterTests");
    if (Directory.Exists(root)) Directory.Delete(root, true);
    Directory.CreateDirectory(root);
    try
    {
      string package = Environment.GetEnvironmentVariable("TUFHELPER_LITE_PACKAGE_UNDER_TEST");
      if (string.IsNullOrWhiteSpace(package))
        package = CreatePackage(root, "0.1.4");

      VerifyLegacyBinary(root, package);
      VerifyVersionContract();
      VerifyReleaseSelection();
      VerifyMetadataLimit(root);
      VerifyRuntimeInstall(root, package);
      VerifyLegacySeedAndStateRecovery(root);
      VerifyPayloadFallbackBoundary(root);
      VerifySameRunCandidateResolution(root);
    }
    catch (Exception exception)
    {
      Failures.Add("Unhandled update test exception: " + exception);
    }
    finally
    {
      if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    if (Failures.Count == 0)
    {
      Console.WriteLine("All TUFHelperLite update tests passed.");
      return 0;
    }
    foreach (string failure in Failures) Console.Error.WriteLine(failure);
    return 1;
  }

  private static void VerifyLegacyBinary(string root, string package)
  {
    string fixture = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "TUFHelperLite-0.1.2.dll");
    Equal("official 0.1.2 updater fixture SHA", LegacyFixtureSha256, Sha256(fixture));
    string modRoot = Path.Combine(root, "Legacy");
    Directory.CreateDirectory(modRoot);
    Assembly legacy = Assembly.LoadFrom(fixture);
    Type stager = legacy.GetType("TUFHelperLite.Bootstrap.UpdatePackageStager", true);
    MethodInfo stage = stager.GetMethod("Stage", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    try
    {
      stage.Invoke(null, new object[] { package, modRoot, "0.1.4", Sha256(package) });
      True("official 0.1.2 accepts final transport ZIP",
        File.Exists(Path.Combine(modRoot, "Data", "updates", "pending", "pending.json")));

      Type installerType = legacy.GetType("TUFHelperLite.Bootstrap.PendingUpdateInstaller", true);
      object installer = Activator.CreateInstance(
        installerType,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        null,
        new object[] { modRoot, null, null },
        null);
      MethodInfo apply = installerType.GetMethod(
        "ApplyPending", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
      object applied = apply.Invoke(installer, new object[] { "0.1.2" });
      True("official 0.1.2 applies final transport ZIP", applied != null);
      True("official 0.1.2 applies 0.1.4 core",
        File.Exists(Path.Combine(modRoot, "TUFHelperLite.Core.dll")));
      True("official 0.1.2 applies migration payload",
        File.Exists(Path.Combine(modRoot, "Assets", "AdofaiIpc", "AdofaiIpc.Migration.dll")));
      True("official 0.1.2 applies fixed launcher",
        File.Exists(Path.Combine(modRoot, "Assets", "AdofaiIpc", "TUFHelperLite.Launcher.dll")));

      string infoPath = Path.Combine(modRoot, "Info.json");
      UnityModManager.ModInfo info = JsonConvert.DeserializeObject<UnityModManager.ModInfo>(
        File.ReadAllText(infoPath));
      UnityModManager.ModEntry owner = new(info, modRoot + Path.DirectorySeparatorChar);
      string controls = Path.Combine(modRoot, "Assets", "AdofaiIpc");
      Assembly migration = Assembly.Load(File.ReadAllBytes(Path.Combine(controls, "AdofaiIpc.Migration.dll")));
      Type migrationType = migration.GetType("AdofaiIpc.Migration.TransitionMigration", true);
      MethodInfo prepare = migrationType.GetMethod(
        "Prepare",
        BindingFlags.Static | BindingFlags.Public,
        null,
        new[] { typeof(UnityModManager.ModEntry), typeof(string) },
        null);
      True("0.1.2-applied core can prepare fixed dependency entrypoint",
        prepare.Invoke(null, new object[] { owner, controls }) is true);
      True("migration installs root dependency shim",
        File.Exists(Path.Combine(modRoot, "AdofaiIpc.DependencyShim.dll")));
      True("migration seeds dependency bootstrap state",
        File.Exists(Path.Combine(modRoot, "DependencyBootstrap", "state.json")));
      string migratedInfo = File.ReadAllText(infoPath);
      True("migration switches Info.json last",
        migratedInfo.Contains("AdofaiIpc.DependencyShim.dll") &&
        migratedInfo.Contains("AdofaiIpc.DependencyShim.DependencyShim.Load"));
      MethodInfo commit = installerType.GetMethod(
        "Commit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
      commit.Invoke(installer, new[] { applied });
    }
    catch (TargetInvocationException exception)
    {
      Failures.Add("official 0.1.2 rejected the transport ZIP: " + exception.InnerException);
    }
  }

  private static void VerifyReleaseSelection()
  {
    string json = ReleaseJson("0.1.5", 1234, false);
    UpdateManager.UpdateReleaseSelection selection = UpdateManager.SelectRelease(json, "0.1.4");
    Equal("stable release selected", "0.1.5", selection?.Version);
    True("equal release skipped", UpdateManager.SelectRelease(json, "0.1.5") == null);
    True("prerelease skipped", UpdateManager.SelectRelease(ReleaseJson("0.1.5", 1234, true), "0.1.4") == null);
  }

  private static void VerifyVersionContract()
  {
    const string version = "0.1.4";
    Dictionary<string, object> info = JsonConvert.DeserializeObject<Dictionary<string, object>>(
      File.ReadAllText(Required("TUFHELPER_LITE_INFO_JSON")));
    Equal("Info.json product version", version, info["Version"]?.ToString());

    string launcher = Required("TUFHELPER_LITE_LAUNCHER_DLL");
    string engine = Required("TUFHELPER_LITE_UPDATE_ENGINE_DLL");
    Equal("fixed launcher product version", version,
      FileVersionInfo.GetVersionInfo(launcher).ProductVersion);
    Equal("fixed launcher ABI version", new Version(1, 0, 0, 0),
      AssemblyName.GetAssemblyName(launcher).Version);
    Equal("update engine product version", version,
      FileVersionInfo.GetVersionInfo(engine).ProductVersion);
    Equal("core product version", version,
      FileVersionInfo.GetVersionInfo(Required("TUFHELPER_LITE_CORE_DLL")).ProductVersion);
  }

  private static void VerifyRuntimeInstall(string root, string package)
  {
    string modRoot = Path.Combine(root, "Install");
    Directory.CreateDirectory(modRoot);
    string userData = Path.Combine(modRoot, "Data", "user-data.json");
    Directory.CreateDirectory(Path.GetDirectoryName(userData));
    File.WriteAllText(userData, "preserve-me");
    string runtime = RuntimePackageInstaller.Install(package, modRoot, "0.1.4", Sha256(package));
    RuntimePackageInstaller.ValidateCandidate(runtime, "0.1.4");
    True("runtime core mapped", File.Exists(Path.Combine(runtime, "TUFHelperLite.Core.dll")));
    True("runtime engine mapped", File.Exists(Path.Combine(runtime, "TUFHelperLite.UpdateEngine.dll")));
    True("runtime bootstrap mapped", File.Exists(Path.Combine(runtime, "AdofaiIpc.Bootstrap.dll")));
    True("runtime UI assets mapped", Directory.Exists(Path.Combine(runtime, "Assets")));
    Equal("runtime install preserves user data", "preserve-me", File.ReadAllText(userData));

    string unsafePackage = Path.Combine(root, "unsafe.zip");
    using (FileStream stream = File.Create(unsafePackage))
    using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
      AddText(archive, "TUFHelperLite/../escape", "bad");
    Throws<InvalidDataException>("ZIP traversal rejected",
      () => RuntimePackageInstaller.Install(unsafePackage, Path.Combine(root, "Unsafe"), "0.1.5", Sha256(unsafePackage)));

    string replacementPackage = CreatePackage(root, "0.1.5", "replacement.zip");
    string replacementRoot = Path.Combine(root, "Replacement");
    string invalidTarget = Path.Combine(replacementRoot, "Runtime", "versions", "0.1.5");
    Directory.CreateDirectory(invalidTarget);
    File.WriteAllText(Path.Combine(invalidTarget, "partial"), "interrupted");
    string replacement = RuntimePackageInstaller.Install(
      replacementPackage, replacementRoot, "0.1.5", Sha256(replacementPackage));
    RuntimePackageInstaller.ValidateCandidate(replacement, "0.1.5");
    True("interrupted candidate quarantined",
      Directory.GetDirectories(Path.GetDirectoryName(invalidTarget), "0.1.5.invalid-*").Length == 1);
  }

  private static void VerifyMetadataLimit(string root)
  {
    using HttpClient client = new(new FakeHandler((_, _) =>
      Response(new byte[4 * 1024 * 1024 + 1])));
    string modRoot = Path.Combine(root, "MetadataLimit");
    Directory.CreateDirectory(modRoot);
    Throws<InvalidDataException>("oversized release metadata rejected before buffering",
      () => new UpdateManager(modRoot, client).Resolve("0.1.4"));
  }

  private static void VerifyLegacySeedAndStateRecovery(string root)
  {
    string modRoot = Path.Combine(root, "Seed");
    string control = Path.Combine(modRoot, "Assets", "AdofaiIpc");
    Directory.CreateDirectory(control);
    File.Copy(Required("TUFHELPER_LITE_CORE_DLL"), Path.Combine(modRoot, "TUFHelperLite.Core.dll"));
    File.Copy(Required("TUFHELPER_LITE_UPDATE_ENGINE_DLL"), Path.Combine(control, "TUFHelperLite.UpdateEngine.dll"));
    File.Copy(Required("ADOFAIIPC_BOOTSTRAP_DLL"), Path.Combine(control, "AdofaiIpc.Bootstrap.dll"));
    File.WriteAllText(Path.Combine(modRoot, "Info.json"), "{\"Version\":\"0.1.4\"}");
    string ui = Path.Combine(modRoot, "Assets", "win");
    Directory.CreateDirectory(ui);
    File.WriteAllText(Path.Combine(ui, "ui.bundle"), "asset");

    RuntimeStore store = new(modRoot);
    Throws<InvalidDataException>("runtime state version cannot escape versions root",
      () => store.GetCandidate(".."));
    RuntimeState state = store.LoadAndRepair();
    Equal("legacy seed current", "0.1.4", state.Current);
    RuntimeCandidate current = store.GetCandidate(state.Current);
    True("legacy seed copied UI", File.Exists(Path.Combine(current.RuntimePath, "Assets", "win", "ui.bundle")));

    string interruptedStage = Path.Combine(modRoot, "Runtime", "versions", "0.1.5.stage-interrupted");
    Directory.CreateDirectory(interruptedStage);
    store.LoadAndRepair();
    False("unreferenced interrupted staging directory removed", Directory.Exists(interruptedStage));

    string trial = Path.Combine(modRoot, "Runtime", "versions", "0.1.5");
    CopyDirectory(current.RuntimePath, trial);
    File.WriteAllText(Path.Combine(trial, "Info.json"), "{\"Version\":\"0.1.5\"}");
    state.Trial = "0.1.5";
    store.Save(state);
    state = store.LoadAndRepair();
    True("interrupted trial cleared", state.Trial == null);
    False("interrupted trial removed", Directory.Exists(trial));

    string statePath = Path.Combine(modRoot, "Runtime", "state.json");
    File.Copy(statePath, statePath + ".bak", true);
    File.Delete(statePath);
    Equal("state backup recovered", "0.1.4", store.LoadAndRepair().Current);

    File.Copy(statePath, statePath + ".bak", true);
    File.WriteAllText(statePath, "{broken");
    Equal("corrupt state backup recovered", "0.1.4", store.LoadAndRepair().Current);

    File.Copy(statePath, statePath + ".bak", true);
    File.WriteAllText(statePath, "{\"SchemaVersion\":99,\"Current\":\"0.1.4\"}");
    Equal("invalid state backup recovered", "0.1.4", store.LoadAndRepair().Current);

    File.WriteAllText(statePath, "{broken");
    File.Delete(statePath + ".bak");
    Equal("flat seed recovers state without backup", "0.1.4", store.LoadAndRepair().Current);

    File.Delete(Path.Combine(current.RuntimePath, "TUFHelperLite.Core.dll"));
    File.WriteAllText(statePath, "{\"SchemaVersion\":1,\"Current\":\"0.1.4\"}");
    File.Delete(statePath + ".bak");
    state = store.LoadAndRepair();
    Equal("manual reinstall seed repairs missing current", "0.1.4", state.Current);
    True("manual reinstall seed restores core", File.Exists(store.GetCandidate(state.Current).AssemblyPath));
  }

  private static void VerifySameRunCandidateResolution(string root)
  {
    string package = CreatePackage(root, "0.1.5", "TUFHelperLite-0.1.5.zip");
    byte[] bytes = File.ReadAllBytes(package);
    string checksum = Sha256(package);
    string release = ReleaseJson("0.1.5", bytes.Length, false);
    int packageRequests = 0;
    using HttpClient client = new(new FakeHandler((request, _) =>
    {
      if (request.RequestUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        return Response(release);
      if (request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
        return Response(checksum + "  TUFHelperLite.zip");
      packageRequests++;
      return Response(bytes);
    }));
    string modRoot = Path.Combine(root, "Network");
    Directory.CreateDirectory(modRoot);
    UpdateResult result = new UpdateManager(modRoot, client).Resolve("0.1.4");
    Equal("same-run candidate outcome", UpdateOutcomes.Candidate, result.Outcome);
    Equal("same-run candidate version", "0.1.5", result.Version);
    True("same-run candidate verified", Directory.Exists(result.RuntimePath));
    Equal("same-run package downloaded once", 1, packageRequests);
  }

  private static void VerifyPayloadFallbackBoundary(string root)
  {
    try
    {
      PayloadLoader.Load(Path.Combine(root, "missing.dll"), "Missing.Entry.Load", null);
      Failures.Add("missing payload should fail");
    }
    catch (PayloadLoadException exception)
    {
      False("missing payload is safe to fallback", exception.AssemblyLoaded);
    }

    try
    {
      PayloadLoader.Load(Required("TUFHELPER_LITE_LAUNCHER_DLL"), "Missing.Entry.Load", null);
      Failures.Add("missing entrypoint should fail");
    }
    catch (PayloadLoadException exception)
    {
      True("loaded assembly is unsafe to mix with fallback", exception.AssemblyLoaded);
    }
  }

  private static string CreatePackage(string root, string version, string fileName = "TUFHelperLite.zip")
  {
    string package = Path.Combine(root, fileName);
    using FileStream stream = File.Create(package);
    using ZipArchive archive = new(stream, ZipArchiveMode.Create);
    AddFile(archive, "TUFHelperLite/TUFHelperLite.Core.dll", Required("TUFHELPER_LITE_CORE_DLL"));
    AddFile(archive, "TUFHelperLite/Assets/AdofaiIpc/TUFHelperLite.Launcher.dll",
      Required("TUFHELPER_LITE_LAUNCHER_DLL"));
    AddFile(archive, "TUFHelperLite/Assets/AdofaiIpc/TUFHelperLite.UpdateEngine.dll",
      Required("TUFHELPER_LITE_UPDATE_ENGINE_DLL"));
    AddFile(archive, "TUFHelperLite/Assets/AdofaiIpc/AdofaiIpc.DependencyShim.dll",
      Required("ADOFAIIPC_DEPENDENCY_SHIM_DLL"));
    AddFile(archive, "TUFHelperLite/Assets/AdofaiIpc/AdofaiIpc.Bootstrap.dll",
      Required("ADOFAIIPC_BOOTSTRAP_DLL"));
    AddFile(archive, "TUFHelperLite/Assets/AdofaiIpc/AdofaiIpc.Migration.dll",
      Required("ADOFAIIPC_MIGRATION_DLL"));
    const string bootstrapManifest =
      "{\"MinimumAdofaiIpcVersion\":\"0.3.0\"," +
      "\"AssemblyName\":\"Assets/AdofaiIpc/TUFHelperLite.Launcher.dll\"," +
      "\"EntryMethod\":\"TUFHelperLite.Launcher.EntryPoint.Load\"}";
    AddText(archive, "TUFHelperLite/Assets/AdofaiIpc/AdofaiIpcBootstrap.json", bootstrapManifest);
    AddText(archive, "TUFHelperLite/Assets/win/ui.bundle", "asset");
    AddText(archive, "TUFHelperLite/Info.json", "{\"Version\":\"" + version + "\"}");
    AddText(archive, "TUFHelperLite/AdofaiIpcBootstrap.json", bootstrapManifest);
    AddText(archive, "TUFHelperLite/THIRD_PARTY_NOTICES.md", "notices");
    return package;
  }

  private static string ReleaseJson(string version, long size, bool prerelease) =>
    "{\"tag_name\":\"v" + version + "\",\"draft\":false,\"prerelease\":" +
    prerelease.ToString().ToLowerInvariant() + ",\"assets\":[" +
    "{\"name\":\"TUFHelperLite.zip\",\"browser_download_url\":\"https://github.com/KGH1113/TUFHelperLite/releases/download/v" +
    version + "/TUFHelperLite.zip\",\"size\":" + size + "}," +
    "{\"name\":\"TUFHelperLite.zip.sha256\",\"browser_download_url\":\"https://github.com/KGH1113/TUFHelperLite/releases/download/v" +
    version + "/TUFHelperLite.zip.sha256\",\"size\":80}]}";

  private static HttpResponseMessage Response(string value) =>
    new(HttpStatusCode.OK) { Content = new StringContent(value) };

  private static HttpResponseMessage Response(byte[] value) =>
    new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };

  private static void AddFile(ZipArchive archive, string path, string source)
  {
    ZipArchiveEntry entry = archive.CreateEntry(path);
    using Stream output = entry.Open();
    using FileStream input = File.OpenRead(source);
    input.CopyTo(output);
  }

  private static void AddText(ZipArchive archive, string path, string value)
  {
    ZipArchiveEntry entry = archive.CreateEntry(path);
    using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
    writer.Write(value);
  }

  private static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) ??
    throw new InvalidOperationException("Missing update test input: " + name);

  private static string Sha256(string path)
  {
    using SHA256 sha = SHA256.Create();
    using FileStream stream = File.OpenRead(path);
    return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
  }

  private static void CopyDirectory(string source, string destination)
  {
    Directory.CreateDirectory(destination);
    foreach (string file in Directory.GetFiles(source))
      File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    foreach (string directory in Directory.GetDirectories(source))
      CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
  }

  private static void Equal<T>(string name, T expected, T actual)
  {
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
      Failures.Add(name + ": expected " + expected + ", got " + actual);
  }

  private static void True(string name, bool value)
  {
    if (!value) Failures.Add(name + ": expected true");
  }

  private static void False(string name, bool value)
  {
    if (value) Failures.Add(name + ": expected false");
  }

  private static void Throws<T>(string name, Action action) where T : Exception
  {
    try
    {
      action();
      Failures.Add(name + ": expected " + typeof(T).Name);
    }
    catch (T)
    {
    }
  }

  private sealed class FakeHandler : HttpMessageHandler
  {
    private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;
    public FakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) =>
      _handler = handler;
    protected override Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request,
      CancellationToken cancellationToken) =>
      Task.FromResult(_handler(request, cancellationToken));
  }
}
