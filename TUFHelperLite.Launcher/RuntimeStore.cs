using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace TUFHelperLite.Launcher;

internal sealed class RuntimeStore
{
  private static readonly Regex VersionPattern = new(
    "\\\"Version\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"",
    RegexOptions.CultureInvariant);
  private static readonly Regex CanonicalVersionPattern = new(
    "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
    RegexOptions.CultureInvariant);
  private readonly string _runtimeRoot;
  private readonly string _versionsRoot;
  private readonly string _statePath;
  private readonly string _installPath;

  public RuntimeStore(string installPath)
  {
    _installPath = Path.GetFullPath(installPath);
    _runtimeRoot = Path.Combine(_installPath, "Runtime");
    _versionsRoot = Path.Combine(_runtimeRoot, "versions");
    _statePath = Path.Combine(_runtimeRoot, "state.json");
  }

  public RuntimeState LoadAndRepair()
  {
    string backup = _statePath + ".bak";
    if (!File.Exists(_statePath) && File.Exists(backup))
      File.Move(backup, _statePath);
    if (!File.Exists(_statePath))
      SeedLegacyPayload();
    RuntimeState state;
    try
    {
      state = ReadState(_statePath);
    }
    catch
    {
      bool restoredBackup = false;
      state = null;
      if (File.Exists(backup))
      {
        try
        {
          state = ReadState(backup);
          File.Copy(backup, _statePath, true);
          restoredBackup = true;
        }
        catch
        {
          // Fall through to the flat payload installed by a manual reinstall.
        }
      }
      if (!restoredBackup)
      {
        SeedLegacyPayload();
        state = ReadState(_statePath);
      }
    }
    if (!string.IsNullOrWhiteSpace(state.Trial))
    {
      DeleteUnreferencedRuntime(state.Trial, state);
      state.Trial = null;
      Save(state);
    }
    try
    {
      GetCandidate(state.Current);
    }
    catch
    {
      bool recoveredPrevious = false;
      if (!string.IsNullOrWhiteSpace(state.Previous))
      {
        try
        {
          GetCandidate(state.Previous);
          state.Current = state.Previous;
          state.Previous = null;
          Save(state);
          recoveredPrevious = true;
        }
        catch
        {
          // Fall through to the flat payload installed by a manual reinstall.
        }
      }
      if (!recoveredPrevious)
      {
        SeedLegacyPayload();
        state = ReadState(_statePath);
        GetCandidate(state.Current);
      }
    }
    CleanupVersions(state);
    return state;
  }

  public RuntimeCandidate GetCandidate(string version)
  {
    if (string.IsNullOrWhiteSpace(version))
      throw new InvalidDataException("The runtime version is missing.");
    string path = Path.Combine(_versionsRoot, NormalizeVersion(version));
    return ValidateCandidate(version, path);
  }

  public RuntimeCandidate ValidateCandidate(string version, string runtimePath)
  {
    string expected = Path.GetFullPath(Path.Combine(_versionsRoot, NormalizeVersion(version)));
    string actual = Path.GetFullPath(runtimePath ?? string.Empty);
    if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("The update engine returned an unexpected runtime path.");
    string assembly = Path.Combine(actual, "TUFHelperLite.Core.dll");
    string engine = Path.Combine(actual, "TUFHelperLite.UpdateEngine.dll");
    string info = Path.Combine(actual, "Info.json");
    if (!File.Exists(assembly) || !File.Exists(engine) || !File.Exists(info))
      throw new InvalidDataException("The runtime is incomplete.");
    Match match = VersionPattern.Match(File.ReadAllText(info));
    if (!match.Success || !VersionsEqual(match.Groups[1].Value, version))
      throw new InvalidDataException("The runtime Info.json version is invalid.");
    return new RuntimeCandidate(NormalizeVersion(version), actual);
  }

  public void Promote(RuntimeState state, string version)
  {
    string normalized = NormalizeVersion(version);
    if (!VersionsEqual(state.Current, normalized))
      state.Previous = state.Current;
    state.Current = normalized;
    state.Trial = null;
    Save(state);
    CleanupVersions(state);
  }

  public void Save(RuntimeState state)
  {
    Directory.CreateDirectory(_runtimeRoot);
    string temporary = _statePath + ".tmp";
    string backup = _statePath + ".bak";
    File.WriteAllText(temporary, JsonConvert.SerializeObject(state, Formatting.Indented) + Environment.NewLine, Encoding.UTF8);
    if (File.Exists(_statePath))
    {
      if (File.Exists(backup))
        File.Delete(backup);
      File.Replace(temporary, _statePath, backup, true);
    }
    else
    {
      File.Move(temporary, _statePath);
    }
  }

  private static RuntimeState ReadState(string path)
  {
    RuntimeState state = JsonConvert.DeserializeObject<RuntimeState>(File.ReadAllText(path))
      ?? throw new InvalidDataException("TUFHelperLite runtime state is empty.");
    if (state.SchemaVersion != 1 || string.IsNullOrWhiteSpace(state.Current))
      throw new InvalidDataException("TUFHelperLite runtime state is invalid.");
    return state;
  }

  public void DeleteUnreferencedRuntime(string version, RuntimeState state)
  {
    if (string.IsNullOrWhiteSpace(version) || VersionsEqual(version, state.Current) || VersionsEqual(version, state.Previous))
      return;
    TryDeleteDirectory(Path.Combine(_versionsRoot, NormalizeVersion(version)));
  }

  private void CleanupVersions(RuntimeState state)
  {
    if (!Directory.Exists(_versionsRoot))
      return;
    foreach (string directory in Directory.GetDirectories(_versionsRoot))
    {
      string name = Path.GetFileName(directory);
      if (MatchesVersion(name, state.Current) || MatchesVersion(name, state.Previous) ||
          MatchesVersion(name, state.Trial))
        continue;
      TryDeleteDirectory(directory);
    }
  }

  private void SeedLegacyPayload()
  {
    string info = Path.Combine(_installPath, "Info.json");
    string core = Path.Combine(_installPath, "TUFHelperLite.Core.dll");
    string control = Path.Combine(_installPath, "Assets", "AdofaiIpc");
    string engine = Path.Combine(control, "TUFHelperLite.UpdateEngine.dll");
    string dependencyBootstrap = Path.Combine(control, "AdofaiIpc.Bootstrap.dll");
    if (!File.Exists(info) || !File.Exists(core) || !File.Exists(engine) || !File.Exists(dependencyBootstrap))
      throw new InvalidDataException("The legacy TUFHelperLite payload is incomplete.");

    Match match = VersionPattern.Match(File.ReadAllText(info));
    if (!match.Success)
      throw new InvalidDataException("The legacy TUFHelperLite version is invalid.");
    string version = NormalizeVersion(match.Groups[1].Value);
    string target = Path.Combine(_versionsRoot, version);
    if (Directory.Exists(target))
    {
      try
      {
        ValidateCandidate(version, target);
      }
      catch
      {
        string quarantine = target + ".invalid-" + Guid.NewGuid().ToString("N");
        Directory.Move(target, quarantine);
      }
    }
    if (!Directory.Exists(target))
    {
      string staging = target + ".seed-" + Guid.NewGuid().ToString("N");
      try
      {
        Directory.CreateDirectory(staging);
        File.Copy(core, Path.Combine(staging, "TUFHelperLite.Core.dll"));
        File.Copy(engine, Path.Combine(staging, "TUFHelperLite.UpdateEngine.dll"));
        File.Copy(dependencyBootstrap, Path.Combine(staging, "AdofaiIpc.Bootstrap.dll"));
        File.Copy(info, Path.Combine(staging, "Info.json"));
        CopyPayloadAssets(staging);
        Directory.CreateDirectory(_versionsRoot);
        if (Directory.Exists(target)) TryDeleteDirectory(staging);
        else Directory.Move(staging, target);
      }
      finally
      {
        TryDeleteDirectory(staging);
      }
    }
    ValidateCandidate(version, target);
    Save(new RuntimeState { Current = version });
  }

  private void CopyPayloadAssets(string staging)
  {
    string assets = Path.Combine(_installPath, "Assets");
    if (!Directory.Exists(assets)) return;
    string payloadAssets = Path.Combine(staging, "Assets");
    foreach (string directory in Directory.GetDirectories(assets))
    {
      if (string.Equals(Path.GetFileName(directory), "AdofaiIpc", StringComparison.OrdinalIgnoreCase))
        continue;
      CopyDirectory(directory, Path.Combine(payloadAssets, Path.GetFileName(directory)));
    }
    foreach (string file in Directory.GetFiles(assets))
    {
      Directory.CreateDirectory(payloadAssets);
      File.Copy(file, Path.Combine(payloadAssets, Path.GetFileName(file)));
    }
  }

  private static void CopyDirectory(string source, string destination)
  {
    Directory.CreateDirectory(destination);
    foreach (string file in Directory.GetFiles(source))
      File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    foreach (string directory in Directory.GetDirectories(source))
      CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
  }

  private static string NormalizeVersion(string version)
  {
    string normalized = version.Trim().TrimStart('v', 'V');
    if (!CanonicalVersionPattern.IsMatch(normalized))
      throw new InvalidDataException("The runtime version is invalid.");
    return normalized;
  }

  private static bool VersionsEqual(string left, string right)
  {
    return left != null && right != null &&
           string.Equals(NormalizeVersion(left), NormalizeVersion(right), StringComparison.OrdinalIgnoreCase);
  }

  private static bool MatchesVersion(string directoryName, string version)
  {
    try { return VersionsEqual(directoryName, version); }
    catch { return false; }
  }

  private static void TryDeleteDirectory(string path)
  {
    try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
  }

}
