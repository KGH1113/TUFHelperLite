using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace TUFHelperLite.BundledIpc;

// Also compiled into Core: the released dependency launcher calls Main.Load directly.
// Keep this bridge free of SDK types so it can run before those DLLs are resolved.
internal static class SdkMigration
{
  internal const string PayloadDirectory = "Assets/IpcMigration";
  internal const string LauncherPath = PayloadDirectory + "/TUFHelperLite.Launcher.dll";

  public static void Prepare(string source, string modRoot, bool loadAssemblies = true)
  {
    BundledIpcFiles.Copy(source, source);
    BundledIpcFiles.Copy(source, modRoot);
    BundledIpcFiles.Copy(source, Path.Combine(modRoot, PayloadDirectory));
    string payload = Path.Combine(source, PayloadDirectory);
    string version = (string)JObject.Parse(File.ReadAllText(Path.Combine(source, "Info.json")))["Version"];
    string launcher = LauncherFor(version);
    if (File.Exists(Path.Combine(payload, "TUFHelperLite.UpdateEngine.dll")))
      Replace(Path.Combine(payload, "TUFHelperLite.UpdateEngine.dll"), Path.Combine(source, "TUFHelperLite.UpdateEngine.dll"));
    foreach (string file in new[] { "TUFHelperLite.Launcher.dll", "TUFHelperLite.UpdateEngine.dll" })
    {
      string input = Path.Combine(payload, file);
      if (file == "TUFHelperLite.Launcher.dll" && File.Exists(Path.Combine(source, launcher)))
        input = Path.Combine(source, launcher);
      if (!File.Exists(input)) throw new InvalidDataException("The SDK migration payload is incomplete: " + file);
      if (AssemblyName.GetAssemblyName(input).Name != Path.GetFileNameWithoutExtension(file))
        throw new InvalidDataException("The SDK migration control assembly is invalid: " + file);
      if (FileVersionInfo.GetVersionInfo(input).ProductVersion != version)
        throw new InvalidDataException("The SDK migration control version is invalid: " + file);
      Replace(input, file == "TUFHelperLite.Launcher.dll"
        ? Path.Combine(modRoot, launcher) : Path.Combine(modRoot, PayloadDirectory, file));
    }
    Replace(Path.Combine(source, "TUFHelperLite.UpdateEngine.dll"), Path.Combine(modRoot, "TUFHelperLite.UpdateEngine.dll"));
    // The old launcher's resolver only searches the selected runtime directory.
    if (loadAssemblies)
    {
      Assembly.LoadFrom(Path.Combine(source, "AdofaiIpc.Contracts.dll"));
      Assembly.LoadFrom(Path.Combine(source, "AdofaiIpc.Loader.dll"));
    }
  }

  public static void Commit(string source, string modRoot)
  {
    JObject info = JObject.Parse(File.ReadAllText(Path.Combine(source, "Info.json")));
    if (!BundledIpcFiles.FileContentsEqual(Path.Combine(source, "TUFHelperLite.Core.dll"), Path.Combine(modRoot, "TUFHelperLite.Core.dll")))
      CopyRecoveryAssets(Path.Combine(source, "Assets"), Path.Combine(modRoot, "Assets"));
    Replace(Path.Combine(source, "TUFHelperLite.Core.dll"), Path.Combine(modRoot, "TUFHelperLite.Core.dll"));
    info["AssemblyName"] = LauncherFor((string)info["Version"]);
    info["EntryMethod"] = "TUFHelperLite.Launcher.EntryPoint.Load";
    info.Remove("LoadAfter");
    string target = Path.Combine(modRoot, "Info.json");
    string temporary = target + ".sdk-" + Guid.NewGuid().ToString("N");
    try
    {
      File.WriteAllText(temporary, info.ToString() + Environment.NewLine);
      if (File.Exists(target)) File.Replace(temporary, target, null);
      else File.Move(temporary, target);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
    AppDomain.CurrentDomain.SetData("AdofaiIpc.SdkMigration.TUFHelperLite", true);
  }

  private static void CopyRecoveryAssets(string source, string destination)
  {
    if (!Directory.Exists(source)) return;
    foreach (string file in Directory.GetFiles(source)) Replace(file, Path.Combine(destination, Path.GetFileName(file)));
    foreach (string directory in Directory.GetDirectories(source))
    {
      string name = Path.GetFileName(directory);
      if (name == "AdofaiIpc" || name == "IpcMigration") continue;
      CopyRecoveryAssets(directory, Path.Combine(destination, name));
    }
  }

  internal static string LauncherFor(string version)
  {
    if (!Version.TryParse(version, out Version parsed) || parsed.ToString() != version)
      throw new InvalidDataException("The SDK launcher version is invalid.");
    return PayloadDirectory + "/launchers/" + version + "/TUFHelperLite.Launcher.dll";
  }

  public static void CleanupRetired(string modRoot)
  {
    // The legacy mod only detaches its own directory here. Never traverse links
    // or remove any other game data when cleaning a previous locked DLL.
    try
    {
      string gameRoot = Directory.GetParent(Path.GetFullPath(modRoot).TrimEnd(Path.DirectorySeparatorChar))?.Parent?.FullName;
      if (gameRoot == null) return;
      string retired = Path.Combine(gameRoot, ".adofai-ipc-retired");
      if (!Directory.Exists(retired) || IsLink(retired)) return;
      foreach (string directory in Directory.GetDirectories(retired, "AdofaiIpc-*"))
      {
        try { if (!ContainsLink(directory)) Directory.Delete(directory, true); }
        catch { /* A still-loaded legacy DLL can be retried next launch. */ }
      }
      if (Directory.GetFileSystemEntries(retired).Length == 0) Directory.Delete(retired);
    }
    catch { /* Cleanup never prevents a mod from loading. */ }
  }

  private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

  private static bool ContainsLink(string directory)
  {
    if (IsLink(directory)) return true;
    foreach (string entry in Directory.GetFileSystemEntries(directory))
      if (IsLink(entry) || (Directory.Exists(entry) && ContainsLink(entry))) return true;
    return false;
  }

  private static void Replace(string source, string target)
  {
    if (Path.GetFullPath(source) == Path.GetFullPath(target)) return;
    if (BundledIpcFiles.FileContentsEqual(source, target)) return;
    Directory.CreateDirectory(Path.GetDirectoryName(target));
    string temporary = target + ".sdk-" + Guid.NewGuid().ToString("N");
    try
    {
      File.Copy(source, temporary);
      if (File.Exists(target)) File.Replace(temporary, target, null);
      else File.Move(temporary, target);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
  }
}
